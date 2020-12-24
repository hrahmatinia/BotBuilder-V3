﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Bot.Connector.Shared.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

#if NET45
using System.Configuration;
using System.Diagnostics;
using System.Runtime.Serialization;
#endif

namespace Microsoft.Bot.Connector
{
    public class MicrosoftAppCredentials : ServiceClientCredentials
    {
        /// <summary>
        /// The key for Microsoft app Id.
        /// </summary>
        public const string MicrosoftAppIdKey = "MicrosoftAppId";

        /// <summary>
        /// The key for Microsoft app Password.
        /// </summary>
        public const string MicrosoftAppPasswordKey = "MicrosoftAppPassword";

        protected static readonly ConcurrentDictionary<string, TrustedHostInfo> TrustedHostNames = new ConcurrentDictionary<string, TrustedHostInfo>(
                                                                                        new Dictionary<string, TrustedHostInfo>() {
                                                                                            { "state.botframework.com", new TrustedHostInfo() { DateTime = DateTime.MaxValue } },
                                                                                            { "api.botframework.com", new TrustedHostInfo() { DateTime = DateTime.MaxValue } },
                                                                                            { "token.botframework.com", new TrustedHostInfo() { DateTime = DateTime.MaxValue } }
                                                                                        });
        protected class TrustedHostInfo
        {
            public DateTime DateTime { get; set; }
            /// <summary>
            /// OAuthScopes is a ConcurrentDictionary because they live within a public, static class
            /// container, and values are written during incoming  activity processing, and read during
            /// outgoing activity processing.
            /// </summary>
            public ConcurrentDictionary<string, string> OAuthScopes { get; set; }
        }

#if !NET45
        protected ILogger logger;
#endif 

        private readonly Lazy<AdalAuthenticator> authenticator;

        public MicrosoftAppCredentials(string appId = null, string password = null)
        {
            MicrosoftAppId = appId;
            MicrosoftAppPassword = password;
#if NET45
            if (appId == null)
            {
                MicrosoftAppId = ConfigurationManager.AppSettings[MicrosoftAppIdKey] ?? Environment.GetEnvironmentVariable(MicrosoftAppIdKey, EnvironmentVariableTarget.Process);
            }

            if (password == null)
            {
                MicrosoftAppPassword = ConfigurationManager.AppSettings[MicrosoftAppPasswordKey] ?? Environment.GetEnvironmentVariable(MicrosoftAppPasswordKey, EnvironmentVariableTarget.Process);
            }
#endif
            authenticator = new Lazy<AdalAuthenticator>(() => new AdalAuthenticator(new ClientCredential(MicrosoftAppId, MicrosoftAppPassword)), LazyThreadSafetyMode.ExecutionAndPublication);
        }

#if !NET45
        public MicrosoftAppCredentials(string appId, string password, ILogger logger)
            : this(appId, password)
        {
            this.logger = logger;
        }
#endif

#if !NET45
        public MicrosoftAppCredentials(IConfiguration configuration, ILogger logger = null)
            : this(configuration.GetSection(MicrosoftAppIdKey)?.Value, configuration.GetSection(MicrosoftAppPasswordKey)?.Value, logger)
        {
        }
#endif

        public string MicrosoftAppId { get; set; }
        public string MicrosoftAppPassword { get; set; }

        public static string OAuthEndpoint
        {
            get
            {
                string tenant = null;
#if NET45
                // Advanced user only, see https://aka.ms/bots/tenant-restriction
                tenant = SettingsUtils.GetAppSettings("ChannelAuthTenant");
#endif
                var endpointUrl = string.Format(JwtConfig.ToChannelFromBotLoginUrlTemplate, string.IsNullOrEmpty(tenant) ? "botframework.com" : tenant);

                if (Uri.TryCreate(endpointUrl, UriKind.Absolute, out Uri result))
                    return endpointUrl;

                throw new Exception($"Invalid token endpoint: {endpointUrl}");
            }
        }

        public static string OAuthAuthority
        {
            get
            {
                string tenant = null;
#if NET45
                // Advanced user only, see https://aka.ms/bots/tenant-restriction
                tenant = SettingsUtils.GetAppSettings("ChannelAuthTenant");
#endif
                var authority = string.Format(JwtConfig.ConvergedAppAuthority, string.IsNullOrEmpty(tenant) ? "botframework.com" : tenant);

                if (Uri.TryCreate(authority, UriKind.Absolute, out Uri result))
                    return authority;

                throw new Exception($"Invalid token endpoint: {authority}");
            }
        }

        public static string OAuthBotScope { get { return JwtConfig.ToChannelFromBotOAuthScope; } }

        /// <summary>
        /// Adds the host of service url to <see cref="MicrosoftAppCredentials"/> trusted hosts.
        /// </summary>
        /// <param name="serviceUrl">The service url</param>
        /// <param name="expirationTime">The expiration time after which this service url is not trusted anymore</param>
        /// <param name="oauthScope">(optional) The scope to use while retrieving the token.  If Null, 
        /// MicrosoftAppCredentials.OAuthBotScope will be used.</param>
        /// <param name="extendedHost">(optional)Extend the required match to include this, beyon the host..</param>
        /// <remarks>If expiration time is not provided, the expiration time will DateTime.UtcNow.AddDays(1).</remarks>
        public static void TrustServiceUrl(string serviceUrl, DateTime expirationTime = default(DateTime), string oauthScope = null, string extendedHost = null)
        {
            try
            {
                var scopes = new ConcurrentDictionary<string, string>();
                if (!string.IsNullOrEmpty(oauthScope))
                {
                    scopes.TryAdd(oauthScope, extendedHost);
                }

                var setExpirationTime = expirationTime;
                if (expirationTime == default(DateTime))
                {
                    // by default the service url is valid for one day
                    setExpirationTime = DateTime.UtcNow.Add(TimeSpan.FromDays(1));
                }
                
                TrustedHostNames.AddOrUpdate(new Uri(serviceUrl).Host,
                                            new TrustedHostInfo
                                            {
                                                DateTime = setExpirationTime,
                                                OAuthScopes = scopes
                                            }, (key, currentValue) => 
                {                      
                    // If the developer has provided the expiration, use it.
                    // Otherwise, do not overwite newer dates with older dates.
                    if (expirationTime != default(DateTime) || currentValue.DateTime < setExpirationTime)
                    {
                        currentValue.DateTime = setExpirationTime;
                    }

                    if (!string.IsNullOrEmpty(oauthScope))
                    {
                        currentValue.OAuthScopes.AddOrUpdate(oauthScope, extendedHost, (wasExtendedHost, newExtendedHost) => { return wasExtendedHost ?? newExtendedHost; });
                    }

                    return currentValue;
                });
                
            }
            catch (Exception)
            {
#if NET45
                Trace.TraceWarning($"Service url {serviceUrl} is not a well formed Uri!");
#endif
            }
        }

        /// <summary>
        /// Checks if the service url is for a trusted host or not.
        /// </summary>
        /// <param name="serviceUrl">The service url</param>
        /// <returns>True if the host of the service url is trusted; False otherwise.</returns>
        public static bool IsTrustedServiceUrl(string serviceUrl)
        {
            Uri uri;
            if (Uri.TryCreate(serviceUrl, UriKind.Absolute, out uri))
            {
                return TrustedUri(uri);
            }
            return false;
        }

        private bool ShouldSetToken(HttpRequestMessage request)
        {
            if (TrustedUri(request.RequestUri))
            {
                return true;
            }

#if NET45
            Trace.TraceWarning($"Service url {request.RequestUri.Authority} is not trusted and JwtToken cannot be sent to it.");
#else
            logger?.LogWarning($"Service url {request.RequestUri.Authority} is not trusted and JwtToken cannot be sent to it.");
#endif
            return false;
        }

        /// <summary>
        /// Apply the credentials to the HTTP request.
        /// </summary>
        /// <param name="request">The HTTP request.</param><param name="cancellationToken">Cancellation token.</param>
        public override async Task ProcessHttpRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ShouldSetToken(request))
            {
                string oauthScope = null;
                TrustedHostInfo trustedHostInfo;
                if (TrustedHostNames.TryGetValue(request.RequestUri.Host, out trustedHostInfo))
                {
                    // Some parent bot hosting environments have the same baseurl for multiple parent bots 
                    // and must be routed with an extended url.

                    // This code determines the correct parent bot id, or OAuthScope, by checking known scopes
                    // for the one containing the extended path comparison.
                    var scopes = trustedHostInfo.OAuthScopes;
                    if (scopes.Count > 0)
                    {
                        if (scopes.Count > 1)
                        {
                            string requestUriPath = request.RequestUri.AbsolutePath;
                            foreach(var scope in scopes)
                            {
                                if (requestUriPath.Contains(scope.Value))
                                {
                                    oauthScope = scope.Key;
                                    break;
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(oauthScope))
                        {
                            oauthScope = scopes.First().Key;
                        }
                    }
                }
                
                var authResult = await GetTokenAsync(oauthScope: oauthScope).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authResult);
            }
            await base.ProcessHttpRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> GetTokenAsync(bool forceRefresh = false, string oauthScope = null)
        {
            var token = await authenticator.Value.GetTokenAsync(forceRefresh, oauthScope).ConfigureAwait(false);
            return token.AccessToken;
        }

        private void LogWarning(string warning)
        {
#if NET45
            Trace.TraceWarning(warning);
#else
            logger?.LogWarning(warning);
#endif
        }

        private void LogError(string error)
        {
#if NET45
            Trace.TraceError(error);
#else
            logger?.LogError(error);
#endif
        }

        private static bool TrustedUri(Uri uri)
        {
            TrustedHostInfo trustedHostInfo;
            if (TrustedHostNames.TryGetValue(uri.Host, out trustedHostInfo))
            {
                // check if the trusted service url is still valid
                if (trustedHostInfo.DateTime > DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(5)))
                {
                    return true;
                }
            }
            return false;
        }

#if NET45
        [Serializable]
#endif
        public sealed class OAuthException : Exception
        {
            public OAuthException(string body, Exception inner)
                : base(body, inner)
            {
            }

#if NET45
            private OAuthException(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
            }
#endif
        }
    }
}
