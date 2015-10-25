﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Devices.Applications.RemoteMonitoring.Common.Configurations;
using Microsoft.Azure.Devices.Applications.RemoteMonitoring.Common.DeviceSchema;
using Newtonsoft.Json.Linq;
using Microsoft.Azure.Devices.Applications.RemoteMonitoring.Common.Helpers;

namespace Microsoft.Azure.Devices.Applications.RemoteMonitoring.Common.Utility
{
    public class DocDbRestUtility : IDocDbRestUtility
    {
        //DocDB Rest documentation: https://msdn.microsoft.com/en-us/library/azure/dn781481.aspx

        private readonly string _docDbEndpoint;
        private readonly string _docDbKey;
        private readonly string _dbName;
        private string _dbId;
        private readonly string _collectionName;
        private string _collectionId;

        private const string AUTHORIZATION_HEADER_KEY = "authorization";
        private const string DATABASE_RESOURCE_TYPE = "dbs";
        private const string COLLECTION_RESOURCE_TYPE = "colls";
        private const string DOCUMENTS_RESOURCE_TYPE = "docs";

        private const string APPLICATION_JSON = "application/json";
        private const string X_MS_VERSION = "2015-08-06";

        public DocDbRestUtility(IConfigurationProvider configProvider)
        {
            _docDbEndpoint = configProvider.GetConfigurationSettingValue("docdb.EndpointUrl");
            _docDbKey = configProvider.GetConfigurationSettingValue("docdb.PrimaryAuthorizationKey");
            _dbName = configProvider.GetConfigurationSettingValue("docdb.DatabaseId");
            _collectionName = configProvider.GetConfigurationSettingValue("docdb.DocumentCollectionId");
        }

        public async Task InitializeDatabase()
        {
            string endpoint = string.Format("{0}dbs", _docDbEndpoint);
            string queryString = "SELECT * FROM dbs db WHERE (db.id = @id)";
            var queryParams = new Dictionary<string, object>();
            queryParams.Add("@id", _dbName);

            string topResponse;
            using (WebClient client = BuildWebClient())
            {
                topResponse = await QueryDocDbInternal(client, endpoint, queryString, queryParams, DATABASE_RESOURCE_TYPE, "");
            }

            object topJson = JObject.Parse(topResponse);

            IEnumerable databases = ReflectionHelper.GetNamedPropertyValue(topJson, "Databases", true, false) as IEnumerable;

            if (databases != null)
            {
                foreach (object database in databases)
                {
                    if (database != null)
                    {
                        object id = ReflectionHelper.GetNamedPropertyValue(database, "id", true, false);

                        if ((id != null) && (id.ToString() == this._dbName))
                        {
                            object rid = ReflectionHelper.GetNamedPropertyValue(database, "_rid", true, false);

                            if (rid != null)
                            {
                                this._dbId = rid.ToString();
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(_dbId))
            {
                await CreateDatabase();
                await CreateDeviceCollection();
            }
        }

        private async Task CreateDatabase()
        {
            string response;

            string endpoint = string.Format("{0}dbs", _docDbEndpoint);
            JObject body = new JObject();
            body.Add("id", _dbName);
            using (WebClient client = BuildWebClient())
            {
                client.Headers.Add(AUTHORIZATION_HEADER_KEY, GetAuthorizationToken("POST", DATABASE_RESOURCE_TYPE, ""));
                response = await AzureRetryHelper.OperationWithBasicRetryAsync<string>(async () =>
                    await client.UploadStringTaskAsync(endpoint, "POST", body.ToString())); 

                object json = JObject.Parse(response);

                _dbId = ReflectionHelper.GetNamedPropertyValue(json, "_rid", true, false).ToString();
            }
        }

        public async Task InitializeDeviceCollection()
        {
            IEnumerable collections;
            string topResponse;

            string endpoint = string.Format("{0}dbs/{1}/colls", _docDbEndpoint, _dbId);
            string queryString = "SELECT * FROM colls c WHERE (c.id = @id)";
            var queryParams = new Dictionary<string, object>();
            queryParams.Add("@id", this._collectionName);

            using (WebClient client = BuildWebClient())
            {
                topResponse = await QueryDocDbInternal(client, endpoint, queryString, queryParams, COLLECTION_RESOURCE_TYPE, _dbId);
            }

            object topJson = JObject.Parse(topResponse);

            collections = ReflectionHelper.GetNamedPropertyValue(topJson, "DocumentCollections", true, false) as IEnumerable;

            if (collections != null)
            {
                foreach (object col in collections)
                {
                    object id = ReflectionHelper.GetNamedPropertyValue(col, "id", true, false);

                    if ((id != null) && (id.ToString() == this._collectionName))
                    {
                        object rid = ReflectionHelper.GetNamedPropertyValue(col, "_rid", true, false);

                        if (rid != null)
                        {
                            this._collectionId = rid.ToString();
                            return;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(_collectionId))
            {
                await CreateDeviceCollection();
            }
        }

        private async Task CreateDeviceCollection()
        {
            string endpoint = string.Format("{0}dbs/{1}/colls", _docDbEndpoint, _dbId);
            var body = new JObject();
            body.Add("id", _collectionName);
            using (WebClient client = BuildWebClient())
            {
                client.Headers.Add(AUTHORIZATION_HEADER_KEY, GetAuthorizationToken("POST", COLLECTION_RESOURCE_TYPE, _dbId));
                string response = await AzureRetryHelper.OperationWithBasicRetryAsync<string>(async () =>
                    await client.UploadStringTaskAsync(endpoint, "POST", body.ToString())); 

                object json = JObject.Parse(response);

                _collectionId = ReflectionHelper.GetNamedPropertyValue(json, "_rid", true, false).ToString();
            }
        }

        /// <summary>
        /// Queries the device collection
        /// https://msdn.microsoft.com/en-us/library/azure/dn783363.aspx
        /// </summary>
        /// <param name="queryString"></param>
        /// <param name="queryParameters"></param>
        /// <returns>One page of device results, with metadata</returns>
        public async Task<DocDbRestQueryResult> QueryDeviceManagementCollectionAsync(
            string queryString, Dictionary<string, Object> queryParams, int pageSize = -1, string continuationToken = null)
        {
            if (string.IsNullOrWhiteSpace(queryString))
            {
                throw new ArgumentException("queryString is null or whitespace");
            }

            using (WebClient client = BuildWebClient())
            {
                string endpoint = string.Format("{0}dbs/{1}/colls/{2}/docs", _docDbEndpoint, _dbId, _collectionId);

                var result = new DocDbRestQueryResult();

                string response = await QueryDocDbInternal(client, endpoint, queryString, queryParams, DOCUMENTS_RESOURCE_TYPE, _collectionId, pageSize, continuationToken);
                JObject responseJobj = JObject.Parse(response);
                JToken documents = responseJobj.GetValue("Documents");
                if (documents != null)
                {
                    result.Documents = (JArray)documents;
                }

                WebHeaderCollection responseHeaders = client.ResponseHeaders;

                string count = responseHeaders["x-ms-item-count"];
                if (!string.IsNullOrEmpty(count))
                {
                    result.TotalDocuments = int.Parse(count);
                }
                result.ContinuationToken = responseHeaders["x-ms-continuation"];

                return result;
            }
        }

        private async Task<string> QueryDocDbInternal(WebClient preparedWebClient, string endpoint, string queryString, Dictionary<string, Object> queryParams, 
            string resourceType, string resourceId, int pageSize = -1, string continuationToken = null)
        {
            if (preparedWebClient == null)
            {
                throw new ArgumentNullException("preparedWebClient");
            }

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("endpoint is null or whitespace");
            }

            if (string.IsNullOrWhiteSpace(queryString))
            {
                throw new ArgumentException("queryString is null or whitespace");
            }

            preparedWebClient.Headers.Set("Content-Type", "application/query+json");
            preparedWebClient.Headers.Add(AUTHORIZATION_HEADER_KEY, GetAuthorizationToken("POST", resourceType, resourceId));
            preparedWebClient.Headers.Add("x-ms-documentdb-isquery", "true");

            if (pageSize >= 0)
            {
                preparedWebClient.Headers.Add("x-ms-max-item-count", pageSize.ToString());
            }
            if (continuationToken != null && continuationToken.Length > 0)
            {
                preparedWebClient.Headers.Add("x-ms-continuation", continuationToken);
            }

            var body = new JObject();
            body.Add("query", queryString);
            if (queryParams != null && queryParams.Count > 0)
            {
                var paramsArray = new JArray();
                foreach (string key in queryParams.Keys)
                {
                    var param = new JObject();
                    param.Add("name", key);
                    param.Add("value", JToken.FromObject(queryParams[key]));
                    paramsArray.Add(param);
                }
                body.Add("parameters", paramsArray);
            }

            var result = new DocDbRestQueryResult();

            return await AzureRetryHelper.OperationWithBasicRetryAsync<string>(async () => await preparedWebClient.UploadStringTaskAsync(endpoint, "POST", body.ToString())); 
        }

        public async Task<JObject> SaveNewDeviceAsync(dynamic device)
        {
            using (WebClient client = BuildWebClient())
            {
                client.Headers.Add(AUTHORIZATION_HEADER_KEY, GetAuthorizationToken("POST", DOCUMENTS_RESOURCE_TYPE, _collectionId));

                string endpoint = string.Format("{0}dbs/{1}/colls/{2}/docs", _docDbEndpoint, _dbId, _collectionId);

                if (device.id == null)
                {
                    device.id = Guid.NewGuid().ToString();
                }

                string response = await AzureRetryHelper.OperationWithBasicRetryAsync<string>(async () =>
                    await client.UploadStringTaskAsync(endpoint, "POST", device.ToString()));

                return JObject.Parse(response);
            }
        }

        /// <summary>
        /// Update the record for an existing device.
        /// </summary>
        /// <param name="updatedDevice"></param>
        /// <returns></returns>
        public async Task<JObject> UpdateDeviceAsync(dynamic updatedDevice)
        {
            string rid = DeviceSchemaHelper.GetDocDbRid(updatedDevice);

            using (WebClient client = BuildWebClient())
            {
                client.Headers.Add(AUTHORIZATION_HEADER_KEY, GetAuthorizationToken("PUT", DOCUMENTS_RESOURCE_TYPE, rid));

                string endpoint = string.Format("{0}dbs/{1}/colls/{2}/docs/{3}", _docDbEndpoint, _dbId, _collectionId, rid);

                string response = await AzureRetryHelper.OperationWithBasicRetryAsync<string>(async () =>
                    await client.UploadStringTaskAsync(endpoint, "PUT", updatedDevice.ToString()));

                return JObject.Parse(response);
            }
        }

        /// <summary>
        /// Remove a device from the DocumentDB. If it succeeds the method will return asynchronously.
        /// If it fails for any reason it will let any exception thrown bubble up.
        /// </summary>
        /// <param name="device"></param>
        /// <returns></returns>
        public async Task DeleteDeviceAsync(dynamic device)
        {
            string rid = DeviceSchemaHelper.GetDocDbRid(device);

            using (WebClient client = BuildWebClient())
            {
                client.Headers.Add(AUTHORIZATION_HEADER_KEY, GetAuthorizationToken("DELETE", DOCUMENTS_RESOURCE_TYPE, rid));

                string endpoint = string.Format("{0}dbs/{1}/colls/{2}/docs/{3}", _docDbEndpoint, _dbId, _collectionId, rid);

                await AzureRetryHelper.OperationWithBasicRetryAsync(async () =>
                    await client.UploadStringTaskAsync(endpoint, "DELETE", ""));
            }
        }

        /// <summary>
        /// Builds the necessary headers and adds them to the WebClient that will be used for the request. This does
        /// NOT include the required Authorization header, which may be different for various requests and must be
        /// handled by the calling method before making the request
        /// </summary>
        /// <param name="webClient">Required: The WebClient that will be used for the request. 
        /// The headers will be added to this client</param>
        /// <param name="pageItemCount">Optional: If the request will be made in pages this is the
        /// number of items per page</param>
        /// <param name="continuationToken">Optional: If the request will be made in pages, and you have a continuation token 
        /// from a previous page, this will ensure the next page begins at the right place</param>
        private WebClient BuildWebClient()
        {
            var webClient = new WebClient();
            webClient.Encoding = System.Text.Encoding.UTF8;
            webClient.Headers.Add("Content-Type", APPLICATION_JSON);
            webClient.Headers.Add("Accept", APPLICATION_JSON);
            webClient.Headers.Add("x-ms-version", X_MS_VERSION);

            // The date of the request, as specified in RFC 1123. The date format is expressed in
            // Coordinated Universal Time (UTC), for example. Fri, 08 Apr 2015 03:52:31 GMT.
            webClient.Headers.Add("x-ms-date", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture));

            return webClient;
        }

        /// <summary>
        /// Build up the authorization string that should be put in the authorization header on the WebClient.
        /// This header is required for all requests
        /// </summary>
        /// <param name="requestVerb">GET, PUT, POST, DELETE, etc</param>
        /// <param name="resourceType">The type of resource you are accessing -- colls for
        /// collections, for example</param>
        /// <param name="resourceId">The resource ID provided in the Azure portal. It should be a
        /// very short hash-looking string similar to jNHDTMVaDgB=</param>
        /// <returns></returns>
        [SuppressMessage(
            "Microsoft.Globalization", 
            "CA1308:NormalizeStringsToUppercase",
            Justification = "Token signatures are base on lower-case strings.")]
        private string GetAuthorizationToken(string requestVerb, string resourceType, string resourceId)
        {
            // https://msdn.microsoft.com/en-us/library/azure/dn783368.aspx
            // The date portion of the string is the date and time the message was sent
            // (in "HTTP-date" format as defined by RFC 7231 Date/Time Formats) e.g. Tue, 15 Nov 1994 08:12:31 GMT.
            string dateString = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);

            string signatureRaw = 
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}\n{1}\n{2}\n{3}\n\n", 
                     requestVerb.ToLowerInvariant(), 
                     resourceType.ToLowerInvariant(), 
                     resourceId.ToLowerInvariant(), 
                     dateString.ToLowerInvariant());

            byte[] sigBytes = Encoding.UTF8.GetBytes(signatureRaw);
            byte[] keyBytes = Convert.FromBase64String(_docDbKey);

            byte[] hashBytes;
            using (HashAlgorithm algo = new HMACSHA256(keyBytes))
            {
                hashBytes = algo.ComputeHash(sigBytes);
            }

            string hashString = Convert.ToBase64String(hashBytes);

            return Uri.EscapeDataString(string.Format(CultureInfo.InvariantCulture, "type=master&ver=1.0&sig={0}", hashString));
        }
    }
}
