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

        private CacheMigrationSettings _cacheMigrationSettings;

        public SharedTokenCacheClientFactory() { }

        /// <summary>
        /// Initialize the client factory with token cache migration settings. Factory will try to migrate the cache before any access to token cache.
        /// </summary>
        public SharedTokenCacheClientFactory(CacheMigrationSettings cacheMigrationSettings) => _cacheMigrationSettings = cacheMigrationSettings;

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
                        client.UserTokenCache.SetBeforeWrite(BeforeWriteNotification);
                    }
                });
            }
            else
            {
                var cacheHelper = GetCacheHelper(client.AppConfig.ClientId);
                cacheHelper.RegisterCache(client.UserTokenCache);
                client.UserTokenCache.SetBeforeWrite(BeforeWriteNotification);
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

        private void BeforeWriteNotification(TokenCacheNotificationArgs args)
        {
            if (AzureSession.Instance.TokenCache != null)
            {
                AzureSession.Instance.TokenCache.CacheData = args.TokenCache?.SerializeMsalV3();
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
            var cacheHelper = GetCacheHelper(PowerShellClientId);
            cacheHelper.Clear();
            AzureSession.Instance.TokenCache?.Clear();
        }
    }
}
