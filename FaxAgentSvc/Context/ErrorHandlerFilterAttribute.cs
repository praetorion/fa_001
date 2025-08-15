namespace FaxAgentSvc.Context
{
    using FXC6.Entity.Api;
    using System.Net;
    using System.Net.Http;
    using System.Web.Http.Filters;

    public class ErrorHandlerFilterAttribute : ExceptionFilterAttribute
    {
        public override void OnException(HttpActionExecutedContext context)
        {
            var result = new ResponseModel<object>
            {
                Status = "Error",
                Data = null,
                Message = context.Exception.Message
            };
            context.Response = context.Request.CreateResponse(HttpStatusCode.InternalServerError, result);
        }
    }
}