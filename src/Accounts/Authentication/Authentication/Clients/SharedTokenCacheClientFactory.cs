﻿// ----------------------------------------------------------------------------------
//
// Copyright Microsoft Corporation
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ----------------------------------------------------------------------------------

using Hyak.Common;
using Microsoft.Azure.Commands.Common.Authentication.Authentication.Clients;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.Azure.Commands.Common.Authentication
{
    public class SharedTokenCacheClientFactory : AuthenticationClientFactory
    {
        public static readonly string CacheFilePath =
            Path.Combine(SharedUtilities.GetUserRootDirectory(), ".IdentityService", "msal.cache");

        public override byte[] ReadTokenData()
        {
            return TryReadTokenFromFileCache(CacheFilePath);
        }

        public override void FlushTokenData()
        {
            WriteTokenToFileCache(_tokenCacheDataToFlush, CacheFilePath);
            base.FlushTokenData();
        }

        private CacheMigrationSettings _cacheMigrationSettings;


        /// <exception cref="MsalCachePersistenceException">When the operating system does not support persistence.</exception>
        public SharedTokenCacheClientFactory()
        {
            VerifyCachePersistence();
        }

        /// <summary>
        /// Initialize the client factory with token cache migration settings. Factory will try to migrate the cache before any access to token cache.
        /// </summary>
        /// <exception cref="MsalCachePersistenceException">When the operating system does not support persistence.</exception>
        public SharedTokenCacheClientFactory(CacheMigrationSettings cacheMigrationSettings) : this() =>
            _cacheMigrationSettings = cacheMigrationSettings;

        /// <summary>
        /// Verify if token cache persistence is available on current system. Throws exception if not.
        /// </summary>
        /// <exception cref="MsalCachePersistenceException">When the operating system does not support persistence.</exception>
        protected void VerifyCachePersistence()
        {
            GetCacheHelper(PowerShellClientId).VerifyPersistence();
        }

        public override void RegisterCache(IClientApplicationBase client)
        {
            if (_cacheMigrationSettings != null)
            {
                // register a one-time handler to deserialize token cache
                client.UserTokenCache.SetBeforeAccess((TokenCacheNotificationArgs args) =>
                {
                    try
                    {
                        DeserializeTokenCache(args.TokenCache, _cacheMigrationSettings);
                    }
                    catch (Exception e)
                    {
                        // continue silently
                        TracingAdapter.Information($"[SharedTokenCacheClientFactory] Exception caught trying migrating ADAL cache: {e.Message}");
                    }
                    finally
                    {
                        _cacheMigrationSettings = null;
                        // replace the handler with the real one
                        var cacheHelper = GetCacheHelper(client.AppConfig.ClientId);
                        cacheHelper.RegisterCache(client.UserTokenCache);
                    }
                });
            }
            else
            {
                var cacheHelper = GetCacheHelper(client.AppConfig.ClientId);
                cacheHelper.RegisterCache(client.UserTokenCache);
            }
        }

        private void DeserializeTokenCache(ITokenCacheSerializer tokenCache, CacheMigrationSettings cacheMigrationSettings)
        {
            switch (cacheMigrationSettings.CacheFormat)
            {
                case CacheFormat.AdalV3:
                    tokenCache.DeserializeAdalV3(cacheMigrationSettings.CacheData);
                    return;
                case CacheFormat.MsalV3:
                    tokenCache.DeserializeMsalV3(cacheMigrationSettings.CacheData);
                    return;
                default:
                    return;
            }
        }

        private MsalCacheHelper GetCacheHelper(string clientId)
        {
            var builder = new StorageCreationPropertiesBuilder(Path.GetFileName(CacheFilePath), Path.GetDirectoryName(CacheFilePath), clientId);
            builder = builder.WithMacKeyChain(serviceName: "Microsoft.Developer.IdentityService", accountName: "MSALCache");
            builder = builder.WithLinuxKeyring(
                schemaName: "msal.cache",
                collection: "default",
                secretLabel: "MSALCache",
                attribute1: new KeyValuePair<string, string>("MsalClientID", "Microsoft.Developer.IdentityService"),
                attribute2: new KeyValuePair<string, string>("MsalClientVersion", "1.0.0.0"));
            var storageCreationProperties = builder.Build();
            return MsalCacheHelper.CreateAsync(storageCreationProperties).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        public override void ClearCache()
        {
            base.ClearCache();
            var cacheHelper = GetCacheHelper(PowerShellClientId);
            cacheHelper.Clear();
        }
    }
}
