﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Client.ADO.Parameters;
using ClickHouse.Client.Formats;
using ClickHouse.Client.Utility;

namespace ClickHouse.Client.ADO
{
    public class ClickHouseConnection : DbConnection, IClickHouseConnection, ICloneable
    {
        private const string CustomSettingPrefix = "set_";
        private static readonly HttpClientHandler DefaultHttpClientHandler;

        private readonly IHttpClientFactory httpClientFactory;
        private readonly HttpClient httpClient;
        private readonly string httpClientName;
        private readonly ConcurrentDictionary<string, object> customSettings = new ConcurrentDictionary<string, object>();
        private ConnectionState state = ConnectionState.Closed; // Not an autoproperty because of interface implementation
        private Version serverVersion;
        private string database = "default";
        private string username;
        private string password;
        private bool useCompression;
        private string session;
        private TimeSpan timeout;
        private Uri serverUri;
        private FeatureFlags supportedFeatures;

        static ClickHouseConnection()
        {
            DefaultHttpClientHandler = new HttpClientHandler() { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        }

        public ClickHouseConnection()
            : this(string.Empty)
        {
        }

        public ClickHouseConnection(string connectionString)
        {
            ConnectionString = connectionString;
            httpClient = new HttpClient(DefaultHttpClientHandler, disposeHandler: false);
            httpClient.Timeout = timeout;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClickHouseConnection"/> class using provided HttpClient.
        /// Note that HttpClient must have AutomaticDecompression enabled if compression is not disabled in connection string
        /// </summary>
        /// <param name="connectionString">Connection string</param>
        /// <param name="httpClient">instance of HttpClient</param>
        public ClickHouseConnection(string connectionString, HttpClient httpClient)
        {
            ConnectionString = connectionString;
            this.httpClient = httpClient;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClickHouseConnection"/> class using an HttpClient generated by the provided <paramref name="httpClientFactory"/>.
        /// </summary>
        /// <param name="connectionString">The ClickHouse connection string.</param>
        /// <param name="httpClientFactory">The factory to be used for creating the clients.</param>
        /// <param name="httpClientName">
        /// The name of the HTTP client you want to be created using the provided factory.
        /// If left empty, the default client will be created.
        /// </param>
        /// <remarks>
        /// <list type="bullet">
        /// <item>
        /// If compression is not disabled in the <paramref name="connectionString"/>, the <paramref name="httpClientFactory"/>
        /// must be configured to enable <see cref="HttpClientHandler.AutomaticDecompression"/> for its generated clients.
        /// <example>
        /// For example you can do this while registering the HTTP client:
        /// <code>
        /// services.AddHttpClient("ClickHouseClient").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        /// {
        ///     AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        /// });
        /// </code>
        /// </example>
        /// </item>
        /// <item>
        /// The <paramref name="httpClientFactory"/> must set the timeout for its clients if needed.
        /// <example>
        /// For example you can do this while registering the HTTP client:
        /// <code>
        /// services.AddHttpClient("ClickHouseClient", c => c.Timeout = TimeSpan.FromMinutes(5));
        /// </code>
        /// </example>
        /// </item>
        /// </list>
        /// </remarks>
        public ClickHouseConnection(string connectionString, IHttpClientFactory httpClientFactory, string httpClientName = "")
        {
            ConnectionString = connectionString;
            this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            this.httpClientName = httpClientName ?? throw new ArgumentNullException(nameof(httpClientName));
        }

        /// <summary>
        /// Gets or sets string defining connection settings for ClickHouse server
        /// Example: Host=localhost;Port=8123;Username=default;Password=123;Compression=true
        /// </summary>
        public sealed override string ConnectionString
        {
            get
            {
                var builder = new ClickHouseConnectionStringBuilder
                {
                    Database = database,
                    Username = username,
                    Password = password,
                    Host = serverUri?.Host,
                    Port = (ushort)serverUri?.Port,
                    Compression = useCompression,
                    UseSession = session != null,
                    Timeout = timeout,
                };

                foreach (var kvp in CustomSettings)
                    builder[CustomSettingPrefix + kvp.Key] = kvp.Value;

                return builder.ToString();
            }

            set
            {
                var builder = new ClickHouseConnectionStringBuilder() { ConnectionString = value };
                database = builder.Database;
                username = builder.Username;
                password = builder.Password;
                serverUri = new UriBuilder(builder.Protocol, builder.Host, builder.Port).Uri;
                useCompression = builder.Compression;
                session = builder.UseSession ? builder.SessionId ?? Guid.NewGuid().ToString() : null;
                timeout = builder.Timeout;

                foreach (var key in builder.Keys.Cast<string>().Where(k => k.StartsWith(CustomSettingPrefix)))
                {
                    CustomSettings.Set(key.Replace(CustomSettingPrefix, string.Empty), builder[key]);
                }
            }
        }

        public IDictionary<string, object> CustomSettings => customSettings;

        public override ConnectionState State => state;

        public override string Database => database;

        public override string DataSource { get; }

        public override string ServerVersion => serverVersion?.ToString();

        /// <summary>
        /// Gets enum describing which ClickHouse features are available on this particular server version
        /// Requires connection to be in Open state
        /// </summary>
        public virtual FeatureFlags SupportedFeatures
        {
            get => state == ConnectionState.Open ? supportedFeatures : throw new InvalidOperationException();
            private set => supportedFeatures = value;
        }

        public override DataTable GetSchema() => GetSchema(null, null);

        public override DataTable GetSchema(string type) => GetSchema(type, null);

        public override DataTable GetSchema(string type, string[] restrictions) => SchemaDescriber.DescribeSchema(this, type, restrictions);

        /// <summary>
        /// Warning: implementation-specific API. Exposed to allow custom optimizations
        /// May change in future versions
        /// </summary>
        /// <param name="sql">SQL query to add to URL, may be empty</param>
        /// <param name="data">Raw stream to be sent. May contain SQL query at the beginning. May be gzip-compressed</param>
        /// <param name="isCompressed">indicates whether "Content-Encoding: gzip" header should be added</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Task-wrapped HttpResponseMessage object</returns>
        public async Task PostStreamAsync(string sql, Stream data, bool isCompressed, CancellationToken token)
        {
            var builder = CreateUriBuilder(sql);
            using var postMessage = new HttpRequestMessage(HttpMethod.Post, builder.ToString());
            AddDefaultHttpHeaders(postMessage.Headers);

            postMessage.Content = new StreamContent(data);
            postMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (isCompressed)
            {
                postMessage.Content.Headers.Add("Content-Encoding", "gzip");
            }

            using var response = await GetHttpClient().SendAsync(postMessage, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            await HandleError(response, sql).ConfigureAwait(false);
        }

        internal async Task<HttpResponseMessage> PostSqlQueryAsync(string sqlQuery, CancellationToken token, ClickHouseParameterCollection parameters = null)
        {
            var uriBuilder = CreateUriBuilder();
            if (parameters != null)
            {
                await EnsureOpenAsync(); // Preserve old behavior
                if (SupportedFeatures.HasFlag(FeatureFlags.SupportsHttpParameters))
                {
                    foreach (ClickHouseDbParameter parameter in parameters)
                        uriBuilder.AddQueryParameter(parameter.ParameterName, HttpParameterFormatter.Format(parameter));
                }
                else
                {
                    var formattedParameters = new Dictionary<string, string>(parameters.Count);
                    foreach (ClickHouseDbParameter parameter in parameters)
                        formattedParameters.TryAdd(parameter.ParameterName, InlineParameterFormatter.Format(parameter));
                    sqlQuery = SubstituteParameters(sqlQuery, formattedParameters);
                }
            }
            string uri = uriBuilder.ToString();

            using var postMessage = new HttpRequestMessage(HttpMethod.Post, uri);
            AddDefaultHttpHeaders(postMessage.Headers);

            HttpContent content = new StringContent(sqlQuery);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/sql");
            if (useCompression)
            {
                content = new CompressedContent(content, DecompressionMethods.GZip);
            }

            postMessage.Content = content;

            var response = await GetHttpClient().SendAsync(postMessage, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return await HandleError(response, sqlQuery).ConfigureAwait(false);
        }

        private static async Task<HttpResponseMessage> HandleError(HttpResponseMessage response, string query)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw ClickHouseServerException.FromServerResponse(error, query);
            }
            return response;
        }

        private static string SubstituteParameters(string query, IDictionary<string, string> parameters)
        {
            var builder = new StringBuilder(query.Length);

            var paramStartPos = query.IndexOf('{');
            var paramEndPos = -1;

            while (paramStartPos != -1)
            {
                builder.Append(query.Substring(paramEndPos + 1, paramStartPos - paramEndPos - 1));

                paramStartPos += 1;
                paramEndPos = query.IndexOf('}', paramStartPos);
                var param = query.Substring(paramStartPos, paramEndPos - paramStartPos);
                var delimiterPos = param.LastIndexOf(':');
                if (delimiterPos == -1)
                    throw new NotSupportedException($"param {param} doesn`t have data type");
                var name = param.Substring(0, delimiterPos);

                if (!parameters.TryGetValue(name, out var value))
                    throw new ArgumentOutOfRangeException($"Parameter {name} not found in parameters list");

                builder.Append(value);

                paramStartPos = query.IndexOf('{', paramEndPos);
            }

            builder.Append(query.Substring(paramEndPos + 1, query.Length - paramEndPos - 1));

            return builder.ToString();
        }

        public override void ChangeDatabase(string databaseName) => database = databaseName;

        public object Clone() => new ClickHouseConnection(ConnectionString);

        public override void Close() => state = ConnectionState.Closed;

        public override void Open() => OpenAsync().ConfigureAwait(false).GetAwaiter().GetResult();

        public override async Task OpenAsync(CancellationToken token)
        {
            if (State == ConnectionState.Open)
                return;
            const string versionQuery = "SELECT version() FORMAT TSV";
            try
            {
                var response = await PostSqlQueryAsync(versionQuery, token).ConfigureAwait(false);
                response = await HandleError(response, versionQuery);
                var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

                if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B) // Check if response starts with GZip marker
                    throw new InvalidOperationException("ClickHouse server returned compressed result but HttpClient did not decompress it. Check HttpClient settings");

                if (data.Length == 0)
                    throw new InvalidOperationException("ClickHouse server did not return version, check if the server is functional");

                serverVersion = ParseVersion(Encoding.UTF8.GetString(data).Trim());
                SupportedFeatures = GetFeatureFlags(serverVersion);
                state = ConnectionState.Open;
            }
            catch
            {
                state = ConnectionState.Broken;
                throw;
            }
        }

        public new ClickHouseCommand CreateCommand() => new ClickHouseCommand(this);

        internal static Version ParseVersion(string versionString)
        {
            if (string.IsNullOrWhiteSpace(versionString))
                throw new ArgumentException($"'{nameof(versionString)}' cannot be null or whitespace.", nameof(versionString));
            var parts = versionString.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0)
                .ToArray();
            if (parts.Length == 0 || parts[0] == 0)
                throw new InvalidOperationException($"Invalid version: {versionString}");
            return new Version(parts.ElementAtOrDefault(0), parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2), parts.ElementAtOrDefault(3));
        }

        internal static FeatureFlags GetFeatureFlags(Version serverVersion)
        {
            FeatureFlags flags = 0;
            if (serverVersion > new Version(19, 11, 3, 11))
            {
                flags |= FeatureFlags.SupportsHttpParameters;
            }
            if (serverVersion > new Version(20, 1, 2, 4))
            {
                flags |= FeatureFlags.SupportsDateTime64;
            }
            if (serverVersion > new Version(20, 5))
            {
                flags |= FeatureFlags.SupportsInlineQuery;
            }
            if (serverVersion > new Version(20, 0))
            {
                flags |= FeatureFlags.SupportsDecimal;
                flags |= FeatureFlags.SupportsIPv6;
            }
            if (serverVersion > new Version(21, 0))
            {
                flags |= FeatureFlags.SupportsUUIDParameters;
            }
            if (serverVersion > new Version(21, 1, 2))
            {
                flags |= FeatureFlags.SupportsMap;
            }
            if (serverVersion > new Version(21, 12))
            {
                flags |= FeatureFlags.SupportsBool;
            }

            return flags;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => CreateCommand();

        private void AddDefaultHttpHeaders(HttpRequestHeaders headers)
        {
            headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            if (useCompression)
            {
                headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            }
        }

        private ClickHouseUriBuilder CreateUriBuilder(string sql = null) => new ClickHouseUriBuilder(serverUri)
        {
            Database = database,
            SessionId = session,
            UseCompression = useCompression,
            CustomParameters = customSettings,
            Sql = sql,
        };

        private Task EnsureOpenAsync() => state != ConnectionState.Open ? OpenAsync() : Task.CompletedTask;

        private HttpClient GetHttpClient() => httpClientFactory?.CreateClient(httpClientName) ?? httpClient;
    }
}
