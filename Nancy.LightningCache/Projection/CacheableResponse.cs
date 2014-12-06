using System;

namespace Nancy.LightningCache.Projection
{
    /// <summary>
    /// Cacheable Nancy Response
    /// </summary>
    public class CacheableResponse : Response
    {
        private readonly Response _response;

        /// <summary>
        /// The point in time where the item was cached
        /// </summary>
        public readonly DateTime Created;
        public readonly DateTime Expiration;

        public CacheableResponse(Response response, DateTime expiration) : this(response, expiration, DateTime.Now) { }

        public CacheableResponse(Response response, DateTime expiration, DateTime created)
        {
            _response = response;
            Created = created;
            Expiration = expiration;
            this.ContentType = response.ContentType;
            this.Headers = response.Headers;
            this.StatusCode = response.StatusCode;
            this.Contents = _response.Contents;
        }
    }
}
