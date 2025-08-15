namespace FaxAgentSvc.Context
{
    using Autofac;
    using Autofac.Integration.WebApi;
    using Newtonsoft.Json.Serialization;
    using Owin;
    using System.Linq;
    using System.Net.Http.Formatting;
    using System.Reflection;
    using System.Web.Http;

    public class Startup
    {
        public IContainer Container { get; private set; }

        public void Configure(IAppBuilder app)
        {
            var config = new HttpConfiguration();
            Container = CreateContainer(config);
            ConfigureHttp(config, Container);
            ConfigureAuth(app, Container);

            app.UseAutofacMiddleware(Container);
            app.UseWebApi(config);
        }

        private void ConfigureAuth(IAppBuilder app, IContainer container)
        {
            // TODO: ronald add the authorization when the documentation is now complete
        }

        private IContainer CreateContainer(HttpConfiguration config)
        {
            var builder = new ContainerBuilder();
            builder.RegisterType<FaxServerHostContext>().AsSelf().SingleInstance();
            builder.RegisterType<EtherFaxHostContext>().AsSelf().SingleInstance();
            builder.RegisterApiControllers(Assembly.GetExecutingAssembly());
            builder.RegisterWebApiFilterProvider(config);
            builder.RegisterHttpRequestMessage(config);

            return builder.Build();
        }

        private void ConfigureHttp(HttpConfiguration config, IContainer container)
        {
            config.MapHttpAttributeRoutes();
            config.Routes.MapHttpRoute(
                name: "FaxAgentApi",
                routeTemplate: "api/{controller}/{action}/{id}",
                defaults: new { id = RouteParameter.Optional }
            );
            var jsonFormatter = config.Formatters.OfType<JsonMediaTypeFormatter>().First();
            jsonFormatter.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
            config.Formatters.Remove(config.Formatters.XmlFormatter);
            config.DependencyResolver = new AutofacWebApiDependencyResolver(container);
            config.Filters.Add(new ErrorHandlerFilterAttribute());
        }
    }
}