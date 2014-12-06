using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Linq;
using System.Net.Http.Headers;
using Nancy.Bootstrapper;
using Nancy.LightningCache.CacheKey;
using Nancy.LightningCache.CacheStore;
using Nancy.LightningCache.Projection;
using Nancy.Routing;

namespace Nancy.LightningCache
{
    /// <summary>
    /// Asynchronous cache for Nancy 
    /// </summary>
    public class LightningCache
    {
        private static readonly string NO_REQUEST_CACHE_KEY  = "_lightningCacheDisabled";

        private static ICacheStore _cacheStore;
        private static ICacheKeyGenerator _cacheKeyGenerator;

        private static bool _enabled;

        private static INancyEngine _nancyEngine;
        private static INancyEngine NancyEngine
        {
            get
            {
                _nancyEngine = _nancyEngine ?? _nancyBootstrapper.GetEngine();
                return _nancyEngine;
            }
        }
        private static IRouteResolver _routeResolver;

        private static INancyBootstrapper _nancyBootstrapper;


        /// <summary>
        /// 
        /// </summary>
        /// <param name="nancyBootstrapper"></param>
        /// <param name="routeResolver"></param>
        /// <param name="pipelines"></param>
        /// <param name="varyParams"> </param>
        public static void Enable(INancyBootstrapper nancyBootstrapper, IRouteResolver routeResolver, IPipelines pipelines, string[] varyParams)
        {
            Enable(nancyBootstrapper, routeResolver, pipelines, new DefaultCacheKeyGenerator(varyParams), new WebCacheStore());
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="nancyBootstrapper"></param>
        /// <param name="routeResolver"></param>
        /// <param name="pipelines"></param>
        /// <param name="cacheKeyGenerator"></param>
        public static void Enable(INancyBootstrapper nancyBootstrapper, IRouteResolver routeResolver, IPipelines pipelines, ICacheKeyGenerator cacheKeyGenerator)
        {
            Enable(nancyBootstrapper, routeResolver, pipelines, cacheKeyGenerator, new WebCacheStore());
        }



        /// <summary>
        /// 
        /// </summary>
        /// <param name="nancyBootstrapper"></param>
        /// <param name="routeResolver"></param>
        /// <param name="pipeline"></param>
        /// <param name="cacheKeyGenerator"></param>
        /// <param name="cacheStore"></param>
        public static void Enable(INancyBootstrapper nancyBootstrapper, IRouteResolver routeResolver, IPipelines pipeline, ICacheKeyGenerator cacheKeyGenerator, ICacheStore cacheStore)
        {
            if (_enabled)
                return;
            _enabled = true;
            _cacheKeyGenerator = cacheKeyGenerator;
            _cacheStore = cacheStore;
            _nancyBootstrapper = nancyBootstrapper;
            _routeResolver = routeResolver;
            pipeline.BeforeRequest.AddItemToStartOfPipeline(CheckCache);
            pipeline.AfterRequest.AddItemToEndOfPipeline(SetCache);
        }

        /// <summary>
        /// Invokes pre-requirements such as authentication and stuff for the supplied context
        /// reference: https://github.com/NancyFx/Nancy/blob/master/src/Nancy/Routing/DefaultRequestDispatcher.cs
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        private static Response InvokePreRequirements(NancyContext context)
        {
            var resolution = _routeResolver.Resolve(context);
            var preRequirements = resolution.Before;
            var task = preRequirements.Invoke(context, new CancellationToken(false));
            task.Wait();
            return context.Response;
        }

        /// <summary>
        /// checks current cachestore for a cached response and returns it
        /// </summary>
        /// <param name="context"></param>
        /// <returns>cached response or null</returns>
        private static Response CheckCache(NancyContext context)
        {
            if (context.Request.Query is DynamicDictionary)
                if ((context.Request.Query as DynamicDictionary).ContainsKey(NO_REQUEST_CACHE_KEY))
                    return null;

            // Check for the no-cache Cache Control header and a max-age equal to 0
            CacheControlHeaderValue cacheControlOptions = null;
            var headers = context.Request.Headers;

            if (headers != null && headers.CacheControl != null)
            {
                var cacheControlHeader = String.Join<String>(", ",context.Request.Headers.CacheControl);
                CacheControlHeaderValue.TryParse(cacheControlHeader, out cacheControlOptions);
            }
            TimeSpan? maxAge = null;
            TimeSpan? minFresh = null;
            TimeSpan? maxStale = null;

            if (cacheControlOptions != null)
            {
                if (cacheControlOptions.NoCache)
                {
                    return null;
                }

                //Assume MaxAge of 0 = no-cache
                maxAge = cacheControlOptions.MaxAge;
                if (maxAge != null && maxAge.HasValue && 0 == (int)cacheControlOptions.MaxAge.Value.TotalSeconds)
                {
                    return null;
                }

                minFresh = cacheControlOptions.MinFresh;
                maxStale = cacheControlOptions.MaxStaleLimit;
            }

            var key = _cacheKeyGenerator.Get(context.Request);

            if (string.IsNullOrEmpty(key))
                return null;

            var response = _cacheStore.Get(key);

            if (response == null)
                return null;

            /* RFC2616 14.9.3
             * max-age:
             * Indicates that the client is willing to accept a response whose age is no greater than the
             * specified time in seconds. Unless max- stale directive is also included, the client is not
             * willing to accept a stale response.
             * 
             * min-fresh:
             * Indicates that the client is willing to accept a response whose freshness lifetime is no
             * less than its current age plus the specified time in seconds. That is, the client wants a
             * response that will still be fresh for at least the specified number of seconds.
             * 
             * max-stale:
             * Indicates that the client is willing to accept a response that has exceeded its expiration
             * time. If max-stale is assigned a value, then the client is willing to accept a response
             * that has exceeded its expiration time by no more than the specified number of seconds.
             * If no value is assigned to max-stale, then the client is willing to accept a stale response
             * of any age.
             * 
             * For summary of intended strategy see: http://msdn.microsoft.com/en-us/library/27w3sx5e%28v=vs.110%29.aspx
             */

            // See if our request is within maxStale
            bool maxStaleSet = false;
            var expiration = response.Expiration;

            // Make sure our request is within maxAge
            if (maxAge != null
                && maxAge.HasValue
                && response.Created.Add(maxAge.Value) < DateTime.Now)
            {
                return null;
            }

            //MaxStale
            if (maxStale != null && maxStale.HasValue)
            {
                maxStaleSet = true;
                expiration = expiration.Add(maxStale.Value);
            }

            if (expiration < DateTime.Now)
            {
                // If we're over Max Stale, we need a new response now
                if (maxStaleSet)
                {
                    return null;
                }
                // Otherwise, keep our lazy caching policy
                else
                {
                    var t = new Thread(HandleRequestAsync);
                    t.Start(context.Request);
                }
            }

            // Make sure our request is within minFresh
            if (minFresh != null && minFresh.HasValue && expiration.Subtract(minFresh.Value) < DateTime.Now)
            {
                return null;
            }
            
            //make damn sure the pre-requirements are met before returning a cached response
            var preResponse = InvokePreRequirements(context);
            if (preResponse != null)
                return preResponse;

            return response;
        }

        /// <summary>
        /// caches response before it is sent to client if it is a CacheableResponse or if the NegotationContext has the nancy-lightningcache header set.
        /// </summary>
        /// <param name="context"></param>
        private static void SetCache(NancyContext context)
        {
            if (context.Response is CachedResponse)
                return;

            // Check for the no-store Cache Control header
            CacheControlHeaderValue cacheControlOptions = null;
            var headers = context.Request.Headers;

            if (headers != null && headers.CacheControl != null)
            {
                var cacheControlHeader = String.Join<String>(", ", context.Request.Headers.CacheControl);
                CacheControlHeaderValue.TryParse(cacheControlHeader, out cacheControlOptions);
            }

            if (cacheControlOptions != null && cacheControlOptions.NoStore)
            {
                return;
            }

            var key = _cacheKeyGenerator.Get(context.Request);

            if (string.IsNullOrEmpty(key))
                return;

            if (context.Response is CacheableResponse)
            {
                if (context.Response.StatusCode != HttpStatusCode.OK)
                {
                    _cacheStore.Remove(key);
                    return;
                }
                _cacheStore.Set(key, context, (context.Response as CacheableResponse).Expiration);
            }
            else if (context.NegotiationContext != null && context.NegotiationContext.Headers != null && context.NegotiationContext.Headers.ContainsKey("nancy-lightningcache"))
            {
                if (context.Response.StatusCode != HttpStatusCode.OK)
                {
                    _cacheStore.Remove(key);
                    return;
                }
                var expiration = DateTime.Parse(context.NegotiationContext.Headers["nancy-lightningcache"], CultureInfo.InvariantCulture);
                context.NegotiationContext.Headers.Remove("nancy-lightningcache");
                _cacheStore.Set(key, context, expiration);
            }
        }

        private static readonly Dictionary<string,short> RequestSyncKeys = new Dictionary<string,short>();
        private static readonly object Lock = new object();
        /// <summary>
        /// used to asynchronously cache Nancy Requests
        /// </summary>
        /// <param name="context"></param>
        private static void HandleRequestAsync(object context)
        {
            lock (Lock)
            {
                var request = context as Request;

                if (request == null)
                    return;

                var key = _cacheKeyGenerator.Get(request);

                if (string.IsNullOrEmpty(key))
                    return;

                try
                {
                    if (RequestSyncKeys.ContainsKey(key))
                        return;

                    RequestSyncKeys[key] = 1;

                    request.Query[NO_REQUEST_CACHE_KEY] = NO_REQUEST_CACHE_KEY;

                    var context2 = NancyEngine.HandleRequest(request);

                    if (context2.Response.StatusCode != HttpStatusCode.OK)
                        _cacheStore.Remove(key);
                }
                catch (Exception)
                {
                    _cacheStore.Remove(key);
                }
                finally
                {
                    RequestSyncKeys.Remove(key);
                }
            }
        }
    }
}
