namespace FaxAgentSvc
{
    using Autofac;
    using FaxAgentSvc.Context;
    using Microsoft.Owin.Hosting;
    using System;
    using System.Configuration;
    using System.IO;
    using System.ServiceProcess;

    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main(string[] args)
        {
            if (Console.In != StreamReader.Null)
            {
                if (args.Length > 0 && args[0] == "/console")
                {

                    FaxAgent rAgent = new FaxAgent();
                    rAgent.OnStartConsole(args);

                    Console.ReadLine();
                    rAgent.Stop();
                    return;
                }
            }
            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[]
            {
                new FaxAgent()
            };
            ServiceBase.Run(ServicesToRun);
        }

        static void StartServer()
        {
            try
            {
                var baseAddress = ConfigurationManager.AppSettings.Get("hosturl");
                Console.WriteLine($"ServiceAPI Started @ {baseAddress}");
                var startup = new Startup();
                using (WebApp.Start(baseAddress, startup.Configure))
                {
                    startup.Container.Resolve<FaxServerHostContext>().InitializeAgent();
                    startup.Container.Resolve<EtherFaxHostContext>().InitializeAgent();

                    Console.WriteLine("Press any key to exit");
                    Console.ReadKey();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error occured: {ex}");
            }
            finally
            {
                Console.WriteLine("Closing");
            }
            Console.ReadLine();
        }
    }
}