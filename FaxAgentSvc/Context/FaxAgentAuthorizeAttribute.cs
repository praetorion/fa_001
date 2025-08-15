namespace FaxAgentSvc.Context
{
    using System;
    using System.Configuration;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Web.Http;
    using System.Web.Http.Controllers;

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class FaxAgentAuthorizeAttribute : AuthorizeAttribute
    {
        public override void OnAuthorization(HttpActionContext actionContext)
        {
            var request = actionContext.Request;
            if (HasServiceKey(request))
            {
                if (IsMatchConfigKey(actionContext.Request))
                    base.OnAuthorization(actionContext);
                else
                    throw new HttpResponseException(request.CreateResponse(HttpStatusCode.NotAcceptable));
            }
            else
                throw new HttpResponseException(request.CreateResponse(HttpStatusCode.Unauthorized));
        }

        protected override bool IsAuthorized(HttpActionContext actionContext)
        {
            var request = actionContext.Request;
            return HasServiceKey(request)
                ? IsMatchConfigKey(actionContext.Request)
                : false;
        }

        private bool HasServiceKey(HttpRequestMessage request)
        {
            return request.Headers.Contains("Service-Key");
        }

        private bool IsMatchConfigKey(HttpRequestMessage request)
        {
            var secretKey = ConfigurationManager.AppSettings.Get("api:service_key");
            var requestHeaderKeys = request.Headers.GetValues("Service-Key");
            return requestHeaderKeys.Any(key => key.Equals(secretKey));
        }
    }
}
