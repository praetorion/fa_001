namespace FaxAgentSvc
{
    using Autofac;
    using FaxAgentSvc.Context;
    using FaxCore.Common;
    using FXC.ServiceAgent;
    using FXC.ServiceAgent.Model;
    using FXC6.Broadcast;
    using FXC6.Entity.Api;
    using FXC6.Entity.Fax;
    using FXC6.Entity.Job;
    using FXC6.FaxAgentHost;
    using FXC6.Licensing;
    using FXC6.Secure;
    using log4net;
    using Microsoft.Owin.Hosting;
    using NetMQ;
    using NetMQ.Sockets;
    using Polly;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Diagnostics;
    using System.IO;
    using System.Net;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Security.Cryptography.X509Certificates;
    using System.ServiceProcess;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Timers;
    using System.Xml;

    public partial class FaxAgent : ServiceBase
    {
        #region Declarations
        private IContainer _container;
        private IDisposable _webHostApi;
        private FaxServerHostContext _faxServer;
        private EtherFaxHostContext _etherFax;
        private AgentConfig _config;
        private bool isFARunning, isEFDRRunning, _FAThreadCompleted;

        private readonly ILog log;
        private readonly CControl _license;
        private UdpBroadcaster _broadcaster;
        private readonly RequestClient _request;
        private System.Timers.Timer _timePool, _timeQPick, _timeStatus, _timeFaxTimeout, _timeStatusDR;
        private FileSystemWatcher _watchIn, _watchOut, _watchINDR, _watchOUTDR;
        private List<Channel> _procChannel, _procEfChannel;
        #endregion

        public FaxAgent()
        {
            InitializeComponent();
            log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
            _license = new CControl();
            _request = new RequestClient();
            _config = new AgentConfig();
            _procChannel = new List<Channel>();
            _procEfChannel = new List<Channel>();


            AuthContextSession.Instance.TokenSet += Instance_TokenSet;
            AuthContextSession.Instance.TokenExpired += Instance_TokenExpired;
            Authenticate();
        }

        private void FaxAgentTimeInit()
        {
            CreateWorkingDirectory();

            _timePool = new System.Timers.Timer(new TimeSpan(0, 5, 0).TotalMilliseconds);
            _timePool.Elapsed += TimePool_Elapsed;
            _timeQPick = new System.Timers.Timer(500);
            _timeQPick.Elapsed += TimeQPick_Elapsed;
            _timeStatus = new System.Timers.Timer(5000);
            _timeStatus.Elapsed += TimeStatus_Elapsed;
            _timeFaxTimeout = new System.Timers.Timer();
            _timeFaxTimeout.Elapsed += TimeFaxTimeout_Elapsed;
            _timeStatusDR = new System.Timers.Timer(50000);


            _watchIn = new FileSystemWatcher
            {
                Path = AppDomain.CurrentDomain.BaseDirectory + @"\xfax\in",
                Filter = "*.xml",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            _watchIn.Changed += WatchIn_Changed;

            _watchOut = new FileSystemWatcher
            {
                Path = AppDomain.CurrentDomain.BaseDirectory + @"\xfax\out",
                Filter = "*.xml",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            _watchOut.Changed += WatchOut_Changed;

            //EFDR
            _timeStatusDR = new System.Timers.Timer(5000);
            _timeStatusDR.Elapsed += TimeStatusDR_Elapsed;

            _watchINDR = new FileSystemWatcher
            {
                Path = AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\in",
                Filter = "*.xml",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            _watchINDR.Changed += WatchINDR_Changed;

            _watchOUTDR = new FileSystemWatcher
            {
                Path = AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\out",
                Filter = "*.xml",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.Attributes | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            _watchOUTDR.Changed += WatchOUTDR_Changed;

            _watchIn.EnableRaisingEvents = true;
            _watchINDR.EnableRaisingEvents = true;
            _watchOut.EnableRaisingEvents = true;
            _watchOUTDR.EnableRaisingEvents = true;

            ServicePointManager.ServerCertificateValidationCallback += new RemoteCertificateValidationCallback(ValidateRemoteCertificate);
        }

        /// <summary>
        /// Start the service on console mode (no service manager)
        /// </summary>
        /// <param name="args">/console</param>
        public void OnStartConsole(string[] args)
        {
            //start service
            //OnStart(args);
        }

        protected override void OnStart(string[] args)
        {
        }

        private void FaxAgentSvcStart()
        {
            StartWebApiServer();
#if DEBUG
            Console.WriteLine("[D] loading...");
            Thread.Sleep(3000);
#endif
            Console.WriteLine("begin FA thread...");
            Thread fx = new Thread(new ThreadStart(StartFaxAgentThread));
            fx.Start();

            string efDRLL = _license.RetrieveEtherFaxDR();
#if DEBUG
            efDRLL = "false";//"true";
#endif
            if (efDRLL.Equals("true", StringComparison.InvariantCultureIgnoreCase))
            {
                Console.WriteLine("waiting DR thread to begin...");
                while (!_FAThreadCompleted)
                {
                    Thread.Sleep(1000);
                }

                Console.WriteLine("begin DR thread...");
                Thread dr = new Thread(new ThreadStart(StartEtherFaxDRThread));
                dr.Start();

            }
        }

        protected override void OnStop()
        {
            _timePool.Enabled = false;
            _timeQPick.Enabled = false;
            _timeStatus.Enabled = false;
            _watchIn.EnableRaisingEvents = false;
            _watchOut.EnableRaisingEvents = false;
            _watchINDR.EnableRaisingEvents = false;
            _watchOUTDR.EnableRaisingEvents = false;

            //stop udp broadcast
            try
            {
                //_broadcaster.StopListener();
            }
            catch (Exception err)
            {
                Console.WriteLine("Stop Listener: {0}", err.Message);
            }
            Thread.Sleep(1000);
            foreach (Channel cc in _procChannel)
            {
                cc.DisableTimer();
            }

            if (_faxServer != null)
            {
                _faxServer.StopFaxServer();
            }

            try
            {
                DisposeManageObjects();
            }
            catch (Exception errDisp)
            {
                Console.WriteLine("Unable to dispose object. Exit gracefully. {0}", errDisp.Message);
            }
        }

        private void StartFaxAgentThread()
        {
            //gather license
            int chans = 0;
            try
            {
                string res = _license.RetrieveChannels();
#if DEBUG
                res = _config.IniChannel.ToString();//"20";
#endif
                if (Int32.TryParse(res, out chans))
                {
                    _config.IniChannel = Convert.ToInt32(res);
                    _config.Plk = InfoSecure.FXCEncrypt(_config.IniChannel.ToString(), _config.Guid);
                }
                else
                {
                    log.Error("Invalid lic: " + res);
                    Console.WriteLine("Invalid license: {0}", res);
                    EventLog.WriteEntry("FaxAgent SVC", "Invalid license", EventLogEntryType.Error, 105);
                    _config.IniChannel = 0;
                    _config.Plk = InfoSecure.FXCEncrypt(_config.IniChannel.ToString(), _config.Guid);
                }
            }
            catch (Exception errLic)
            {
                log.Error("load port lic error: " + errLic.Message);
                EventLog.WriteEntry("FaxAgent SVC", "Invalid/Broken License", EventLogEntryType.Error, 105);

#if RELEASE
                _config.IniChannel = 0; 
#endif
                _config.Plk = InfoSecure.FXCEncrypt(_config.IniChannel.ToString(), _config.Guid);
            }

            try
            {
                StartFaxAgent();

                if (isFARunning)
                {
                    if (_config.AgentId.Equals(string.Empty))
                    {
                        //register faxagent
                        log.Info("registering faxagent...");
                        _config.AgentId = RegisterFaxAgent()
                            .GetAwaiter()
                            .GetResult();
                        if (string.IsNullOrEmpty(_config.AgentId))
                        {
                            goto FARegisterFailed;
                        }
                        UpdateConfig("AgentID", _config.AgentId, true);

                        if (!_config.AgentId.Equals(string.Empty))
                        {
                            RegisterFaxChannels(Int32.Parse(_config.AgentId), true);
                            UpdateFaxAgentConfig(Int32.Parse(_config.AgentId), true);
                            GetChannelsConfig(Int32.Parse(_config.AgentId), true);
                            _timeStatus.Enabled = true;
                            //_timeQPick.Enabled = true;  //CHECK SERVER STAT
                            //_timePool.Enabled = true;
                        }
                    }
                    else          //if (!_config.AgentId.Equals(string.Empty))
                    {
                        ResetQueue(_config.AgentId);
                        GetChannelsConfig(Int32.Parse(_config.AgentId), true);
                        _timeStatus.Enabled = true;
                        //_timeQPick.Enabled = true;  //CHECK SERVER STAT
                        //_timePool.Enabled = true;
                    }

                    //connect to fa ws
                    {

                        _request.Execute<InvokeFaxAgentRequestModel, HttpResponseDetail>(
                           new InvokeFaxAgentRequestModel
                           {
                               AgentId = int.Parse(_config.AgentId),
                               LicStatus = _license.GetLicenseStatus()[0].ToString(),
                               EvalInfo = _license.GetTrailPeriod()[0],
                               Plk = _config.Plk,
                               Bc = InfoSecure.FXCEncrypt(_config.BarcodeOn.ToString(), _config.Guid)
                           }, Api.Put.InvokeFaxAgent, RequestType.PUT)
                           .GetAwaiter()
                           .GetResult();

                        log.DebugFormat("lic invoked plk: {0}", _config.Plk);
                        string[] invokedChans = new string[0];

                        var invokedLic = _request.Execute<InvokeFaxChannelRequestModel, ResponseModel<string>>(
                            new InvokeFaxChannelRequestModel
                            {
                                AgentId = int.Parse(_config.AgentId)
                            }, Api.Put.InvokeFaxChannels, RequestType.PUT)
                            .GetAwaiter()
                            .GetResult();

                        if (invokedLic != null && invokedLic.IsSuccess() && !string.IsNullOrEmpty(invokedLic.Data))
                        {
                            invokedChans = InfoSecure.FXCDecrypt(invokedLic.Data, _config.Guid).Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                            log.DebugFormat("invoked channels: {0}", invokedChans.Length);
                        }

                        //channels creation

                        Parallel.For(0, invokedChans.Length, (index) =>
                        {
                            if (chans != 0)
                            {
                                log.DebugFormat("module:{0}", invokedChans[index].ToString());
                                Channel chan = new Channel(_faxServer, _config.AgentId);
                                chan.Interval = _config.Interval;
                                chan.ModuleChannel = invokedChans[index].ToString();
                                chan.Server = _config.Server;
                                chan.ChannelDelay = _config.ChannelDelay;
                                chan.StartMonitorQueue();
                                if (_config.IsRemoteAgent)
                                {
                                    chan.EnableTimer();
                                }
                                //Parallel.Invoke(() => chan.StartMonitorQueue());
                                _procChannel.Add(chan);
                                chan.CheckChannel(true);
                                chans--;
                                Console.WriteLine("Channel created for agent {0} module {1}", _config.AgentId, invokedChans[index].ToString());
                            }
                        });

                        //for (int i = 0; i < invokedChans.Length; i++)
                        //{
                        //    if (chans != 0)
                        //    {
                        //        log.DebugFormat("module:{0}", invokedChans[i].ToString());
                        //        Channel chan = new Channel(_faxServer, _config.AgentId);
                        //        chan.Interval = _config.Interval;
                        //        chan.ModuleChannel = invokedChans[i].ToString();
                        //        chan.Server = _config.Server;
                        //        Parallel.Invoke(() => chan.StartMonitorQueue());
                        //        if (_config.IsRemoteAgent)
                        //        {
                        //            chan.EnableTimer();
                        //        }
                        //        _procChannel.Add(chan);
                        //        chan.CheckChannel();
                        //        chans--;
                        //        Console.WriteLine("Channel created for agent {0} module {1}", _config.AgentId, invokedChans[i].ToString());
                        //    }
                        //}
                        //client.Close(); 
                    }

                    //start watcher
                    _watchOut.InternalBufferSize = _config.BufferSize;
                    _watchOut.EnableRaisingEvents = true;
                    _watchIn.InternalBufferSize = _config.BufferSize;
                    _watchIn.EnableRaisingEvents = true;
                    Console.WriteLine("FaxAgent console service started.");

                    //_broadcaster = new UdpBroadcaster();
                    //_broadcaster.ReceiveEvent += OnBroadcast;
                    //_broadcaster.PortNo = _config.UdpListenPort;
                    //_broadcaster.StartListener();
                    //Console.WriteLine("Broadcast listener running.");

                    log.Debug("faxagent suspended, resume now.");
                    //_faxServer.FaxServerResume(); //resume driver for fax87
                FARegisterFailed:
                    if (string.IsNullOrEmpty(_config.AgentId))
                    {
                        log.Debug("FA registration failed. Start process aborted.");
                        Console.WriteLine("FA registration failed. Start process aborted.");
                    }
                    else
                    {
                        //fax timeout timer initialize
                        _timeFaxTimeout.Interval = new TimeSpan(0, 5, 0).TotalMilliseconds;
                        _timeFaxTimeout.Enabled = true;
                        log.Debug("Start process completed");
                        Console.WriteLine("Start process completed");
                    }
                }
            }
            catch (Exception err)
            {
                log.Error("start fa thread error: " + err.Message);
                Console.WriteLine("start fa thread error: {0}:{1}", err.Message, err.InnerException.ToString());
                EventLog.WriteEntry("FaxAgent Start SVC Error", err.Message, EventLogEntryType.Error, 173);
            }
            finally
            { //start watcher
                _watchOut.InternalBufferSize = _config.BufferSize;
                _watchOut.EnableRaisingEvents = true;
                _watchIn.InternalBufferSize = _config.BufferSize;
                _watchIn.EnableRaisingEvents = true;
                _FAThreadCompleted = true;
            }

            //try
            //{
            //    Parallel.ForEach(_procChannel, channel =>
            //    {
            //        channel.StartSubscriber();
            //    });
            //}
            //catch (Exception err)
            //{
            //    log.Error("start fa channel subscriber error: " + err.Message);
            //    Console.WriteLine("start fa channel subscriber error: {0}:{1}", err.Message, err.InnerException.ToString());
            //    //EventLog.WriteEntry("FaxAgent Start Channel Subscriber Error", err.Message, EventLogEntryType.Error, 173);
            //}
            StartSubscriber();
        }

        private void OnBroadcast(UdpBroadcastEventArgs e)
        {
            try
            {
                BroadcastType type = (BroadcastType)e.Data[0];
                string owner = type.ToString();
                Console.WriteLine("Broadcast Owner: {0}", owner);
                Console.WriteLine("Broadcast String: {0}", Encoding.UTF8.GetString(e.Data));
                Console.WriteLine("Broadcast IP Sender: {0}", e.EndPoint);
                string data = Encoding.UTF8.GetString(e.Data);
                Console.WriteLine("Decoded string: {0}", data);
                log.DebugFormat("[BC]Owner:{0}, EP:{1}", e.Data[0].ToString(), e.EndPoint.Address);
                if (!string.IsNullOrEmpty(data))
                {
                    string[] jobData = data.Split(new char[] { '|' });
                    //assign jobs
                    Console.WriteLine("Job AgentID: {0}", jobData[0]);
                    if (jobData[0] == _config.AgentId)
                    {
                        Parallel.Invoke(() => AssignJob(data, false));
                    }
                    else if (jobData[0] == _config.EtherFaxDRAgentID)
                    {
                        Parallel.Invoke(() => AssignJob(data, true));
                    }
                }
            }
            catch (Exception err)
            {
                Console.WriteLine("OnBroadcastException: {0}", err.ToString());
                log.ErrorFormat("OnBroadcastException: {0}", err.ToString());
            }
        }

        private void AssignJob(string jobInfo, bool isefDR)
        {
            string[] job = jobInfo.Split(new char[] { '|' });
            if (isefDR)
            {
                var chanDR = _procEfChannel.Find(x => x.ModuleChannel.Equals(job[2]));
                if (chanDR != null)
                {
                    if (!chanDR.IsChannelBusy())
                    {
                        Console.WriteLine("Checking channel DR");
                        chanDR.CheckChannel(false); 
                    }
                    else
                    {
                        // add jobInfo to Queue
                        //JobCollection<AssignJobQueue>.Instance.Enqueue(new AssignJobQueue
                        //{
                        //    IsEFDR = isefDR,
                        //    JobInfo = jobInfo
                        //});
                        Console.WriteLine("Channel Busy. waiting...");
                        var func = new Func<Channel,bool>((Channel channel) => channel.IsChannelBusy());
                        func.Wait(chanDR, TimeSpan.FromSeconds(5));
                        Console.WriteLine("Checking Channel DR");
                        chanDR.CheckChannel(false);
                    }
                }
            }
            else
            {
                var chan = _procChannel.Find(x => x.ModuleChannel.Equals(job[2]));
                if (chan != null)
                {
                    if (!chan.IsChannelBusy())
                    {
                        Console.WriteLine("Checking channel");
                        chan.CheckChannel(false); 
                    }
                    else
                    {
                        //// add jobInfo to Queue
                        //JobCollection<AssignJobQueue>.Instance.Enqueue(new AssignJobQueue
                        //{
                        //    IsEFDR = isefDR,
                        //    JobInfo = jobInfo
                        //});

                        Console.WriteLine("Channel Busy. waiting...");
                        var func = new Func<Channel, bool>((Channel channel) => channel.IsChannelBusy());
                        func.Wait(chan, TimeSpan.FromSeconds(5));
                        Console.WriteLine("Checking Channel");
                        chan.CheckChannel(false);
                    }
                }
            }
        }

        private async void StartEtherFaxDRThread()
        {
            int chans = _config.IniChannel;
            StartEtherFax();

            if (isEFDRRunning)
            {
                if (_config.EtherFaxDRAgentID.Equals(string.Empty))
                {
                    //register etherfax as faxagent
                    log.Info("register DR faxagent");
                    Console.WriteLine("[EFDR]register DR faxagent");
                    _config.EtherFaxDRAgentID = await RegisterFaxAgentDR();
                    if (string.IsNullOrEmpty(_config.EtherFaxDRAgentID))
                    {
                        goto EFRegisterFailed;
                    }
                    Console.WriteLine("[EFDR]updating DR faxagent[{0}]", _config.EtherFaxDRAgentID);
                    UpdateConfig("AgentID", _config.EtherFaxDRAgentID, false);
                    if (!_config.EtherFaxDRAgentID.Equals(string.Empty))
                    {
                        RegisterFaxChannels(Int32.Parse(_config.EtherFaxDRAgentID), false);
                        UpdateFaxAgentConfig(Int32.Parse(_config.EtherFaxDRAgentID), false);
                        GetChannelsConfig(Int32.Parse(_config.EtherFaxDRAgentID), false);
                        //skip timer enable
                        //need to update channel status 
                        _timeStatusDR.Enabled = true;
                        //timePoolDR.Enabled = true;
                    }
                }
                else        //if (!_config.EtherFaxDRAgentID.Equals(string.Empty))
                {
                    ResetQueue(_config.EtherFaxDRAgentID);
                    Console.WriteLine("[EFDR]getting channels config");
                    GetChannelsConfig(Int32.Parse(_config.EtherFaxDRAgentID), false);
                    //update channel status timer
                }

                //update lic stat
                {
                    //web service call
                    Console.WriteLine("[EFDR]update license info");
                    await _request.Execute<InvokeFaxAgentRequestModel, HttpResponseDetail>(
                        new InvokeFaxAgentRequestModel
                        {
                            AgentId = int.Parse(_config.EtherFaxDRAgentID),
                            LicStatus = _license.GetLicenseStatus()[0].ToString(),
                            EvalInfo = _license.GetTrailPeriod()[0],
                            Plk = _config.Plk,
                            Bc = InfoSecure.FXCEncrypt(_config.BarcodeOn.ToString(), _config.Guid)
                        }, Api.Put.InvokeFaxAgent, RequestType.PUT);

                    //block channel enable
                    string[] invokedChans = new string[0];
                    var invokedLic = await _request.Execute<InvokeFaxChannelRequestModel, ResponseModel<string>>(
                        new InvokeFaxChannelRequestModel
                        {
                            AgentId = int.Parse(_config.EtherFaxDRAgentID)
                        }, Api.Put.InvokeFaxChannels, RequestType.PUT);

                    if (invokedLic != null && invokedLic.IsSuccess() && !string.IsNullOrEmpty(invokedLic.Data))
                    {
                        invokedChans = InfoSecure.FXCDecrypt(invokedLic.Data, _config.Guid).Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                    }
                    Console.WriteLine("[EFDR]Invoked channel from server: {0}", invokedChans.Length);
                    Parallel.For(0, invokedChans.Length, (index) =>
                    {
                        if (chans != 0)
                        {
                            log.DebugFormat("EFDR module: {0}", invokedChans[index].ToString());
                            Channel chan = new Channel(_etherFax, _config.EtherFaxDRAgentID, true);
                            chan.Interval = _config.Interval;
                            chan.ModuleChannel = invokedChans[index].ToString();
                            chan.Server = _config.Server;
                            if (_config.IsRemoteAgent)
                            {
                                chan.EnableTimer();
                            }
                            Parallel.Invoke(() => chan.StartMonitorQueue());
                            _procEfChannel.Add(chan);
                            chan.CheckChannel(true);
                            chans--;
                            Console.WriteLine("Channel created for agent {0} module {1}", _config.EtherFaxDRAgentID, invokedChans[index].ToString());
                        }
                    });
                    //for (int i = 0; i < invokedChans.Length; i++)
                    //{
                    //    if (chans != 0)
                    //    {
                    //        log.DebugFormat("EFDR module: {0}", invokedChans[i].ToString());
                    //        Channel chan = new Channel(_etherFax, _config.EtherFaxDRAgentID, true);
                    //        chan.Interval = _config.Interval;
                    //        chan.ModuleChannel = invokedChans[i].ToString();
                    //        chan.Server = _config.Server;
                    //        if (_config.IsRemoteAgent)
                    //        {
                    //            chan.EnableTimer();
                    //        }
                    //        Parallel.Invoke(() => chan.StartMonitorQueue());
                    //        _procEfChannel.Add(chan);
                    //        chan.CheckChannel();
                    //        chans--;
                    //        Console.WriteLine("Channel created for agent {0} module {1}", _config.EtherFaxDRAgentID, invokedChans[i].ToString());
                    //    }
                    //}
                    //client.Close(); 
                }
                _watchOUTDR.InternalBufferSize = _config.BufferSize;
                _watchOUTDR.EnableRaisingEvents = true;
                _watchINDR.InternalBufferSize = _config.BufferSize;
                _watchINDR.EnableRaisingEvents = true;
                Console.WriteLine("EDFR FaxAgent console service started.");
                log.Debug("DR faxagent suspended. resume now.");
                //_etherFax.FaxServerResume();
            EFRegisterFailed:
                if (string.IsNullOrEmpty(_config.EtherFaxDRAgentID))
                {
                    log.Debug("EFDR FA register failed. Start process aborted.");
                    Console.WriteLine("EFDR FA register failed. Start process aborted.");
                }
                else
                {
                    log.Debug("EFDR Start process completed.");
                    Console.WriteLine("EFDR Start process completed.");
                }
            }
        }

        private void StartEtherFax()
        {
            _etherFax.LogFileCount = Convert.ToInt32(_config.LogFileCount);
            _etherFax.LogFileSize = Convert.ToInt32(_config.LogFileSize);
            _etherFax.LogLevel = Enums.ALL;
            _etherFax.IniChannels = _config.IniChannel;
            _etherFax.BarCodeScan = _config.BarcodeOn;
            _etherFax.InitializeAgent();

            if (!_etherFax.GetFSIsStarted())
            {
                _etherFax.StartFaxServer(_config.EtherFaxDRAddr.Trim(), _config.EtherFaxDRAcc, _config.EtherFaxDRUsr, _config.EtherFaxDRPwd);
            }

            isEFDRRunning = _etherFax.GetFSIsStarted();
            log.Debug("DR FaxAgent running: " + isEFDRRunning.ToString());
            Console.WriteLine("DR FaxAgent running: " + isEFDRRunning.ToString());
        }

        private void StartFaxAgent()
        {
            log.Info("Starting faxagent...");
            bool isEtherFax = false;
            bool isMonopond = false;

            try
            {
                //load barcode
                string res = _license.RetrieveBC();
                if (res.Equals("true", StringComparison.InvariantCultureIgnoreCase))
                    _config.BarcodeOn = true;
                else
                    _config.BarcodeOn = false;
            }
            catch (Exception exL)
            {
                log.ErrorFormat("error loading BC lic: {0}", exL.Message);
                _config.BarcodeOn = false;
            }
            Console.WriteLine("barcode enable: {0}", _config.BarcodeOn);
            try
            {
                //load lic info
                string[] licStat = _license.GetLicenseStatus();
                string[] evalInfo = _license.GetTrailPeriod();

                if (licStat[0].ToString().ToUpper().Contains("EVALUATION-EXPIRED") && licStat[1].ToString().ToUpper().Equals("FALSE"))
                {
                    string errMsg = string.Format("Evaluation Period Expired. Please contact FaxCore support for assistance[907]. Stat:{0}|{1}",
                      licStat[0].ToString(), licStat[1].ToString());
                    throw new Exception(errMsg);
                }

                string server = Environment.MachineName;

                switch (_config.DriverType.ToUpper())
                {
                    case "BROOKTROUT":
                        _faxServer.FaxAgentDriverType = FaxDriverType.Brooktrout;
                        break;
                    case "EICON":
                        _faxServer.FaxAgentDriverType = FaxDriverType.Eicon;
                        break;
                    case "T38":
                        _faxServer.FaxAgentDriverType = FaxDriverType.T38;
                        break;
                    case "ETHERFAX":
                        _faxServer.FaxAgentDriverType = FaxDriverType.EtherFax;
                        isEtherFax = true;
                        break;
                    case "FAXBACK":
                        _faxServer.FaxAgentDriverType = FaxDriverType.FaxBack;
                        break;
                    case "MONOPOND":
                        _faxServer.FaxAgentDriverType = FaxDriverType.Monopond;
                        isMonopond = true;
                        break;
                    case "SERIAL":
                        _faxServer.FaxAgentDriverType = FaxDriverType.Serial;
                        break;
                    case "XCAPI":
                        _faxServer.FaxAgentDriverType = FaxDriverType.XCapi;
                        break;
                    case "DIVA":
                        _faxServer.FaxAgentDriverType = FaxDriverType.Diva;
                        break;
                    default:
                        _faxServer.FaxAgentDriverType = FaxDriverType.Brooktrout;
                        break;
                }

                switch (_config.LogLevel.ToString().ToUpper())
                {
                    case "ALL":
                        _faxServer.LogLevel = Enums.ALL;
                        break;
                    case "DEBUG":
                        _faxServer.LogLevel = Enums.DEBUG;
                        break;
                    case "ERROR":
                        _faxServer.LogLevel = Enums.ERROR;
                        break;
                    case "FATAL":
                        _faxServer.LogLevel = Enums.FATAL;
                        break;
                    case "WARN":
                        _faxServer.LogLevel = Enums.WARM;
                        break;
                    default:
                        _faxServer.LogLevel = Enums.ALL;
                        break;
                }

                _faxServer.IniChannels = _config.IniChannel;
                _faxServer.BarCodeScan = _config.BarcodeOn;
                _faxServer.LogFileCount = Convert.ToInt32(_config.LogFileCount);
                _faxServer.LogFileSize = Convert.ToInt32(_config.LogFileSize);
                _faxServer.InitializeAgent();

                if (!_faxServer.GetFSIsStarted())
                {
                    if (isEtherFax)
                    {
                        _faxServer.StartFaxServer(_config.EtherFaxAddr.Trim(), _config.EtherFaxAcc.Trim(), _config.EtherFaxUsr.Trim(), _config.EtherFaxPwd.Trim());
                    }
                    else
                    {
                        _faxServer.StartFaxServer();
                    }

                    //require reevaluation
                    isFARunning = _faxServer.GetFSIsStarted();
                    log.Info("ServicePath: " + _faxServer.GetFSServicePath());
                    log.Info("LogLVL: " + _faxServer.LogLevel.ToString());
                    log.Info("DvrType: " + _faxServer.FaxAgentDriverType.ToString());
                    log.Info("AutoAdjustResolution: " + _faxServer.AdjustFaxResolution.ToString());
                    log.Info("Agent status: " + isFARunning);
                }

            }
            catch (Exception err)
            {
                log.Error("FaxAgent Start Error: " + err.Message);
                EventLog.WriteEntry("FaxAgent", "Start FA Error: " + err.Message, EventLogEntryType.Error, 158);
            }
        }

        #region Helper

        private void UpdateConfig(string key, string value, bool isFA)
        {
            log.Info("updating configuration file");
            try
            {
                Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                if (isFA)
                {
                    config.AppSettings.Settings.Remove(key);
                    config.AppSettings.Settings.Add(key, value);
                }
                else
                {
                    XmlDocument xmlDoc = new XmlDocument();
                    xmlDoc.Load(AppDomain.CurrentDomain.BaseDirectory + @"\\faxagent.exe.config");
                    //edit
                    xmlDoc.SelectSingleNode("//Virtual/EtherFaxDR/add[@key='AgentID']").Attributes["value"].Value = value;
                    xmlDoc.Save(AppDomain.CurrentDomain.BaseDirectory + @"\\faxagent.exe.config");
                    ConfigurationManager.RefreshSection("Virtual/EtherFaxDR");
                }

                config.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection("appSettings");
                log.Info("configuration file updated");
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "Configuration Update Error: " + err.Message, EventLogEntryType.Warning, 492);
            }
        }

        private void CreateWorkingDirectory()
        {
            if (!Directory.Exists(AppDomain.CurrentDomain.BaseDirectory + @"\xfax\in"))
            {
                Directory.CreateDirectory(AppDomain.CurrentDomain.BaseDirectory + @"\xfax\in");
            }
            if (!Directory.Exists(AppDomain.CurrentDomain.BaseDirectory + @"\xfax\out"))
            {
                Directory.CreateDirectory(AppDomain.CurrentDomain.BaseDirectory + @"\xfax\out");
            }
            if (!Directory.Exists(AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive"))
            {
                Directory.CreateDirectory(AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive");
            }
            if (!Directory.Exists(AppDomain.CurrentDomain.BaseDirectory + @"\xfax\in\temp"))
            {
                Directory.CreateDirectory(AppDomain.CurrentDomain.BaseDirectory + @"\xfax\in\temp");
            }

            //DR directory creation
            if (!Directory.Exists(AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\in"))
            {
                Directory.CreateDirectory(AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\in");
            }
            if (!Directory.Exists(AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\out"))
            {
                Directory.CreateDirectory(AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\out");
            }
            if (!Directory.Exists(AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\archive"))
            {
                Directory.CreateDirectory(AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\archive");
            }
            if (!Directory.Exists(AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\in\temp"))
            {
                Directory.CreateDirectory(AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\in\temp");
            }
        }

        private void MoveTif(string from, string to)
        {
            try
            {
                if (CheckDirectoryExist(to))
                {
                    string filename = Path.GetFileName(from);
                    //File.Move(from, to + filename);
                    File.Move(from, Path.Combine(to, filename));
                }
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("Move File", err.Message, EventLogEntryType.Error, 408);
            }
        }

        /// <summary>
        /// Check file is ready for processing. Large file require more time to be ready.
        /// </summary>
        /// <param name="path">full file path</param>
        /// <returns></returns>
        public static bool IsFileReady(string path)
        {
            bool result = false;

            try
            {
                using (StreamReader sr = new StreamReader(path))
                {
                    sr.Peek();
                    result = true;
                }
            }
            catch (IOException err)
            {
                Console.WriteLine("File ready exception: {0}", err.Message);
                return false;
            }
            return result;
        }

        private bool CheckDirectoryExist(string dir)
        {
            bool result = false;
            try
            {
                if (Directory.Exists(dir))
                {
                    result = true;
                }
                else
                {
                    Directory.CreateDirectory(dir);
                    result = true;
                }
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("Directory Check", err.Message, EventLogEntryType.Warning, 421);
            }
            return result;
        }

        /// <summary>
        /// Update fax channel state
        /// </summary>
        private async void UpdateChannelStatus()
        {
            _timeStatus.Enabled = false;
            ArrayList lst = _faxServer.GetModuleCount();
            try
            {
                {
                    {
                        await _request.Execute<UpdateFaxAgentConfigRequestModel, HttpResponseDetail>(new UpdateFaxAgentConfigRequestModel
                        {
                            AgentId = Convert.ToInt32(_config.AgentId),
                            ActiveChans = _faxServer.GetFSActiveChannel(),
                            Utilization = _faxServer.GetFSUtilization(),
                            Status = _faxServer.GetFSIsStarted()
                        }, Api.Put.UpdateFaxAgentConfig, RequestType.POST);

                        for (int i = 0; i < lst.Count; i++)
                        {
                            string _module = lst[i].ToString();
                            int _mod = Convert.ToInt32(_module.Remove(0, 1));
                            for (int j = 0; j < _faxServer.GetChannelCount(_mod); j++)
                            {
                                await _request.Execute<UpdateFAChannelStatusRequestModel, HttpResponseDetail>(new UpdateFAChannelStatusRequestModel
                                {
                                    AgentId = Convert.ToInt32(_config.AgentId),
                                    Module = _module,
                                    Port = j,
                                    ConnectTime = _faxServer.GetChanConnectTime(_mod, j),
                                    ConnectSpeed = _faxServer.GetChanConnectSpeed(_mod, j),
                                    CurrentPage = _faxServer.GetChanCurrentPage(_mod, j),
                                    State = _faxServer.GetChanState(_mod, j),
                                    IsActive = _faxServer.GetChanIsActive(_mod, j),
                                    FaActiveChans = _faxServer.GetFSActiveChannel(),
                                    FaUtilization = _faxServer.GetFSUtilization(),
                                    FaIsStarted = _faxServer.GetFSIsStarted(),
                                    RemoteId = _faxServer.GetChanRemoteID(_mod, j)
                                }, Api.Put.UpdateChannelStatus, RequestType.POST);

                                Thread.Sleep(500);
                            }
                        }
                        //status.Close(); 
                    }
                }
            }
            catch (Exception err)
            {
                log.Error("Update Channel status error: " + err.Message);
            }
            finally
            {
                _timeStatus.Enabled = true;
            }
        }

        /// <summary>
        /// Update DR fax channel state
        /// </summary>
        private async void UpdateChannelStatusDR()
        {
            _timeStatusDR.Enabled = false;
            ArrayList lst = _etherFax.GetModuleCount();

            try
            {
                await _request.Execute<UpdateFaxAgentConfigRequestModel, HttpResponseDetail>(new UpdateFaxAgentConfigRequestModel
                {
                    AgentId = Convert.ToInt32(_config.EtherFaxDRAgentID),
                    ActiveChans = _etherFax.GetFSActiveChannel(),
                    Utilization = _etherFax.GetFSUtilization(),
                    Status = _etherFax.GetFSIsStarted()
                }, Api.Put.UpdateFaxAgentConfig, RequestType.POST);

                for (int i = 0; i < lst.Count; i++)
                {
                    string _module = lst[i].ToString();
                    int _mod = Convert.ToInt32(_module.Remove(0, 2));
                    for (int j = 0; j < _etherFax.GetChannelCount(_mod); j++)
                    {
                        await _request.Execute<UpdateFAChannelStatusRequestModel, HttpResponseDetail>(new UpdateFAChannelStatusRequestModel
                        {
                            AgentId = Convert.ToInt32(_config.EtherFaxDRAgentID),
                            Module = _module,
                            Port = j,
                            ConnectTime = _etherFax.GetChanConnectTime(_mod, j),
                            ConnectSpeed = _etherFax.GetChanConnectSpeed(_mod, j),
                            CurrentPage = _etherFax.GetChanCurrentPage(_mod, j),
                            State = _etherFax.GetChanState(_mod, j),
                            IsActive = _etherFax.GetChanIsActive(_mod, j),
                            FaActiveChans = _etherFax.GetFSActiveChannel(),
                            FaUtilization = _etherFax.GetFSUtilization(),
                            FaIsStarted = _etherFax.GetFSIsStarted(),
                            RemoteId = _etherFax.GetChanRemoteID(_mod, j)
                        }, Api.Put.UpdateChannelStatus, RequestType.POST);

                        Thread.Sleep(500);
                    }
                }
            }
            catch (Exception err)
            {
                log.Error("Update Channel status error: " + err.Message);
            }
            finally
            {
                _timeStatusDR.Enabled = true;
            }
        }

        private static bool ValidateRemoteCertificate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors policyError)
        {
            bool result = false;
            if (cert.Subject.ToUpper().Contains("yourservername"))
            {
                result = true;
            }
            return true;// result;
        }

        private string ParseCSID(string escString)
        {
            string result = escString;  //string.Empty;
            try
            {
                result = WebUtility.HtmlDecode(escString);
            }
            catch (Exception err)
            {
                log.Error("ParseCSID error: " + err.Message);
                EventLog.WriteEntry("ParseCSID error for " + escString + "EX: " + err.Message, EventLogEntryType.Error, 1410);
            }

            return result;
        }

        private string GetIPAddress()
        {
            string result = string.Empty;
            try
            {
                var hostIp = Dns.GetHostEntry(GetServerName());
                foreach (var ip in hostIp.AddressList)
                {
                    if (!ip.AddressFamily.Equals(AddressFamily.InterNetworkV6))
                        result = ip.ToString();
                }

                log.Info("Server IP: " + result);
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("Get IP", err.Message, EventLogEntryType.Error, 337);
            }
            return result;
        }

        private string GetServerName()
        {
            string result = Environment.MachineName;
            return result;
        }

        private void DisposeManageObjects()
        {
            if (_container != null)
                _container.Dispose();

            if (_webHostApi != null)
                _webHostApi.Dispose();
        }

        /// <summary>
        /// Reset entire fax queue on start up
        /// </summary>
        /// <param name="ID">agent ID</param>
        private async void ResetQueue(string ID)
        {
            try
            {
                var res = _request.Execute<RescheduleQueueRequestModel, HttpResponseDetail>(new RescheduleQueueRequestModel
                {
                    AgentID = ID,
                    //MsgAge = 0  // 0 = no restriction
                }, Api.Post.RescheduleQueue, RequestType.POST).GetAwaiter().GetResult();

                log.DebugFormat("Reset Q: {0}:{1}", res.StatusCode, res.IsSuccessful);

                #region deprecated
                //SqlConnection thisConnection = new SqlConnection(ConfigurationManager.ConnectionStrings["FaxCoreConnect"].ToString());
                //SqlCommand cmdUpdate = thisConnection.CreateCommand();
                //string qUpdate = @"UPDATE dbo.fxtb_Queue_2 WITH (ROWLOCK) 
                //     SET xPickable = 1 
                //           ,xPickedDate = NULL 
                //     WHERE xMsgTransmitID = {0}";

                //SqlCommand cmdSelect = thisConnection.CreateCommand();
                //cmdSelect.CommandText = string.Format(@"select xMsgTransmitID from dbo.fxtb_Queue_2 where xPickable = 0 and xAgentId = '{0}'", ID);

                //try
                //{
                //    List<string> msgList = new List<string>();
                //    thisConnection.Open();

                //    using (SqlDataReader rdr = cmdSelect.ExecuteReader())
                //    {
                //        while (rdr.Read())
                //        {
                //            msgList.Add(rdr.GetValue(0).ToString());
                //        }
                //    }

                //    foreach (var item in msgList)
                //    {
                //        cmdUpdate.CommandText = string.Format(qUpdate, item);
                //        cmdUpdate.ExecuteNonQuery();
                //    }

                //}
                //catch (Exception err)
                //{
                //    log.Error("Reset Queue Connect Error: " + err.Message);
                //}
                //finally
                //{
                //    thisConnection.Close();
                //    thisConnection.Dispose();
                //} 
                #endregion
            }
            catch (Exception err)
            {
                log.Error("Reset Queue Error: " + err.Message);
            }
        }

        /// <summary>
        /// Reset queue post service start
        /// </summary>
        /// <param name="ID">agent ID</param>
        /// <param name="msgAge">message age parameter. Reset item in queue when older than value</param>
        private async void ResetQueue(string ID, int msgAge)
        {
            try
            {
                var res =  _request.Execute<RescheduleQueueRequestModel, HttpResponseDetail>(new RescheduleQueueRequestModel
                {
                    AgentID = ID,
                    MsgAge = msgAge
                }, Api.Post.RescheduleQueue, RequestType.POST).GetAwaiter().GetResult();

                log.DebugFormat("Reset Q: {0}:{1}", res.StatusCode, res.IsSuccessful);

                #region deprecated
                //var thisConnection = new SqlConnection(ConfigurationManager.ConnectionStrings["FaxCoreConnect"].ToString());
                //var cmdUpdate = thisConnection.CreateCommand();
                //string qUpdate = @"UPDATE dbo.fxtb_Queue_2 WITH (ROWLOCK) 
                //     SET xPickable = 1 
                //           ,xPickedDate = NULL 
                //     WHERE xMsgTransmitID = {0}";

                //var cmdSelect = thisConnection.CreateCommand();
                //cmdSelect.CommandText = string.Format(@"select xMsgTransmitID from dbo.fxtb_Queue_2 where xPickable = 0 and xAgentId = '{0}' and DATEDIFF(minute, xCrDate, GETDATE()) > {1}", ID, msgAge);


                //try
                //{
                //    var msgList = new List<string>();
                //    thisConnection.Open();

                //    using (SqlDataReader rdr = cmdSelect.ExecuteReader())
                //    {
                //        while (rdr.Read())
                //        {
                //            msgList.Add(rdr.GetValue(0).ToString());
                //        }
                //    }

                //    foreach (var item in msgList)
                //    {
                //        cmdUpdate.CommandText = string.Format(qUpdate, item);
                //        cmdUpdate.ExecuteNonQuery();
                //    }

                //}
                //catch (Exception err)
                //{
                //    log.Error("Reset Queue Connect Error: " + err.Message);
                //}
                //finally
                //{
                //    thisConnection.Close();
                //    thisConnection.Dispose();
                //} 
                #endregion
            }
            catch (Exception err)
            {
                log.Error("Reset Queue Error: " + err.Message);
            }
        }

        /// <summary>
        /// Clean up
        /// </summary>
        private void PollingRPC()
        {
            //timePool.Stop();
            _timePool.Enabled = false;
            //System.Threading.Interlocked.Increment(ref poolCountLock);
            try
            {

                log.Debug("Pooling check folder...");
                string outFolder = AppDomain.CurrentDomain.BaseDirectory + @"xfax\out\";
                string inFolder = AppDomain.CurrentDomain.BaseDirectory + @"xfax\in\";

                //check out folder
                DirectoryInfo oF = new DirectoryInfo(outFolder);
                foreach (FileInfo oFile in oF.GetFiles("*.xml", SearchOption.TopDirectoryOnly))
                {
                    log.Debug(string.Format("Looking at file {0} with last write at {1}", oFile.FullName, oFile.LastWriteTime.ToString()));
                    TimeSpan tsO = oFile.LastWriteTime.Subtract(DateTime.Now);
                    log.Debug("File age: " + tsO.TotalMinutes.ToString());
                    if (tsO.TotalMinutes < -15)
                    {
                        try
                        {
                            ProcessOutboundXML(oFile.FullName);
                        }
                        catch (Exception err)
                        {
                            log.Error("WatchOut TS: " + err.ToString());
                            EventLog.WriteEntry("WatchOut TS", err.Message, EventLogEntryType.Error, 79);
                        }
                    }


                }
                //check in folder

                DirectoryInfo iF = new DirectoryInfo(inFolder);
                foreach (FileInfo iFile in iF.GetFiles("*.xml", SearchOption.TopDirectoryOnly))
                {
                    log.Debug(string.Format("Looking at file {0} with last write at {1}", iFile.FullName, iFile.LastWriteTime.ToString()));
                    TimeSpan tsI = iFile.LastWriteTime.Subtract(DateTime.Now);
                    log.Debug("File age: " + tsI.TotalMinutes.ToString());
                    if (tsI.TotalMinutes < -20)
                    {
                        string dateFld = DateTime.Today.ToString("yyyyMMdd");

                        try
                        {
                            ProcessInboundXML(iFile.FullName);
                        }
                        catch (Exception err)
                        {
                            log.Error("Incoming FaxTS: " + err.ToString());
                            EventLog.WriteEntry("Incoming Fax TS", err.Message, EventLogEntryType.Error, 138);
                        }
                        finally
                        {

                        }

                    }
                }
            }

            catch (Exception err)
            {
                log.Error("Pool Timer Err: " + err.Message);
            }
            finally
            {
                //timePool.Start();
                _timePool.Enabled = true;
            }
        }

        #endregion

        #region Registration
        /// <summary>
        /// Register fax channels
        /// </summary>
        /// <param name="agent">agent ID</param>
        /// <param name="isFA">FA or EF indicator. False if ef</param>
        private async void RegisterFaxChannels(int agent, bool isFA)
        {
            log.Info("Register fax channels...");
            ArrayList lst;
            try
            {
                if (isFA)
                {
                    lst = _faxServer.GetModuleCount();
                }
                else
                {
                    lst = _etherFax.GetModuleCount();
                }

                for (int i = 0; i < lst.Count; i++)
                {
                    int channels = 0;
                    string module = lst[i].ToString();
                    if (isFA)
                    {
                        channels = _faxServer.GetChannelCount(int.Parse(module.Remove(0, 1)));
                    }
                    else
                    {
                        channels = _etherFax.GetChannelCount(int.Parse(module.Remove(0, 1)));
                    }
                    log.Info("Channel registration: A:" + agent + "|M:" + module + "|C:" + channels);
                    Thread.Sleep(500); //sleep for half sec to allow SQL to complete channel registraion before moving on to next module. 

                    await _request.Execute<RegisterFaxChannelRequestModel, HttpResponseDetail>(
                        new RegisterFaxChannelRequestModel
                        {
                            AgentId = agent,
                            Module = module,
                            Channels = channels
                        }, Api.Post.RegisterFaxChannel, RequestType.POST);
                }
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent Registration", err.Message, EventLogEntryType.Error, 132);
            }
        }

        /// <summary>
        /// Update fax agent configuration back to main server
        /// </summary>
        /// <param name="agent">agent id</param>
        /// <param name="isFA">false if ef</param>
        private async void UpdateFaxAgentConfig(int agent, bool isFA)
        {
            log.Info("update fax agent configuration...");
            var configPath = "";
            var servicePath = "";

            if (isFA)
            {
                configPath = _faxServer.GetFSConfigurationPath(); //replace value with faxagent service id #todo
                servicePath = _faxServer.GetFSServicePath();
            }
            else
            {
                configPath = _etherFax.GetFSConfigurationPath();
                servicePath = _etherFax.GetFSServicePath();
            }

            await _request.Execute<UpdateFaxAgentConfigStatusRequestModel, HttpResponseDetail>(
                    new UpdateFaxAgentConfigStatusRequestModel
                    {
                        AgentId = agent,
                        ConfigPath = _config.ServiceKey,  //configPath,
                        ServicePath = servicePath
                    }, Api.Put.UpdateFaxAgentStatus, RequestType.PUT);
        }

        /// <summary>
        /// Retrieve channel's config from web server
        /// </summary>
        /// <param name="agent">agent ID</param>
        /// <param name="isFA">FA or DR indicator. False if ef</param>
        private async void GetChannelsConfig(int agent, bool isFA)
        {
            log.Info("get fax channels configuration for FaxAgent: " + agent.ToString());
            int cCount = 0;
            ArrayList lst;
            if (isFA)
            {
                lst = _faxServer.GetModuleCount();
            }
            else
            {
                lst = _etherFax.GetModuleCount();
            }

            FaxReceiveFormats rTifFormat;
            if (_config.RecvFormat.Equals("G3"))
            {
                rTifFormat = FaxReceiveFormats.TiffG3;
            }
            else if (_config.RecvFormat.Equals("G4"))
            {
                rTifFormat = FaxReceiveFormats.TiffG4;
            }
            else
            {
                rTifFormat = FaxReceiveFormats.Default;
            }

            {
                if (isFA)
                {
                    for (int i = 0; i < lst.Count; i++)
                    {
                        int channels = _faxServer.GetChannelCount(int.Parse(lst[i].ToString().Remove(0, 1)));
                        log.Info("Retrieve chan config for module: " + lst[i].ToString() + " , total channels: " + channels);
                        Console.WriteLine("Retrieve agent[{0}] chan config for module: " + lst[i].ToString() + " , total channels: " + channels, agent);
                        for (int j = 0; j < channels; j++)
                        {
                            log.Info("Retrieve channel " + j + " config.");
                            Console.WriteLine("Retrieve channel " + j + " config.");

                            var channel = await _request.Execute<GetChannelConfigRequestModel, ResponseModel<FaxChannel>>(
                                new GetChannelConfigRequestModel
                                {
                                    AgentId = agent,
                                    Module = lst[i].ToString().Remove(0, 1),
                                    Channel = j
                                }, Api.Get.GetFaxConfig, RequestType.GET);

                            log.Info("Channel " + j + " config retrieved");
                            //Console.WriteLine("Channel " + j + " config retrieved");
                            Console.WriteLine("Channel {0} config retrieved status {1}", j, channel.Status);
                            if (channel != null && channel.IsSuccess() && channel.Data != null)
                            {
                                _faxServer.ConfigureAnswerRings(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.AnswerRings.Value);
                                _faxServer.ConfigureDialTimeout(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.DialTimeOut.Value);
                                if (cCount < _config.IniChannel)
                                {
                                    _faxServer.ConfigureEnableChan(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.Enabled.Value);
                                    if (channel.Data.Enabled.Value)
                                    {
                                        cCount++;
                                    }
                                    log.Debug("Ini chans stat: " + j.ToString() + "|" + channel.Data.Enabled.Value.ToString() + "|" + cCount.ToString());
                                    Console.WriteLine("Ini chans stat: " + j.ToString() + "|" + channel.Data.Enabled.Value.ToString() + "|" + cCount.ToString());
                                }
                                try
                                {
                                    //prevent exception being thrown and missed the rest of the configs. Auto-recover with default values.
                                    _faxServer.ConfigureHeaderStyle(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.HeaderStyle, channel.Data.HeaderCustomStr);
                                }
                                catch (Exception erRR)
                                {
                                    log.Error("Set Header style error[auto-default]]: " + erRR.Message);
                                    Console.WriteLine("Set Header style error[auto-default]]: " + erRR.Message);
                                    _faxServer.ConfigureHeaderStyle(int.Parse(lst[i].ToString().Remove(0, 1)), j, "DEFAULT", string.Empty);
                                }
                                _faxServer.ConfigureLocalID(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.LocalCSID);
                                _faxServer.ConfigureReceiveEnabled(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.ReceiveEnabled.Value);
                                _faxServer.ConfigureSendEnabled(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.SendEnabled.Value);
                                _faxServer.ConfigureToneDetectDigits(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.ToneDetectDigits.Value);
                                _faxServer.ConfigureToneDetectEnabled(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.ToneDetectEnabled.Value);
                                _faxServer.ConfigureToneWaitTimeout(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.ToneWaitTimeOut.Value);
                                log.DebugFormat("Channel {0} tone wait timeout: {1}", j.ToString(), channel.Data.ToneWaitTimeOut);
                                Console.WriteLine("Channel {0} tone wait timeout: {1}", j.ToString(), channel.Data.ToneWaitTimeOut);
                                try
                                {
                                    _faxServer.ConfigureDTMFToneType(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.GreetingType);
                                }
                                catch (Exception errTT)
                                {
                                    log.Error("set Tone Type Error[auto-default]: " + errTT.Message);
                                    Console.WriteLine("set Tone Type Error[auto-default]: " + errTT.Message);
                                    _faxServer.ConfigureDTMFToneType(int.Parse(lst[i].ToString().Remove(0, 1)), j, "TONE");
                                }
                                _faxServer.ConfigureDTMFToneWav(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.GreetingWavFile);

                                _faxServer.ConfigureDTMFTerminator(int.Parse(lst[i].ToString().Remove(0, 1)), j, _config.DtmfTerminatorDigit);
                                log.Debug("done channel config: " + j + " channels to go: " + channels);
                                Console.WriteLine("done channel config: " + j + " channels to go: " + channels);
                                _faxServer.ConfigureRecvFormat(int.Parse(lst[i].ToString().Remove(0, 1)), j, rTifFormat);

                            }
                            _faxServer.FaxServerCommit();
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < lst.Count; i++)
                    {
                        int channels = _etherFax.GetChannelCount(int.Parse(lst[i].ToString().Remove(0, 1)));
                        log.Info("Retrieve chan config for module: " + lst[i].ToString() + " , total channels: " + channels);
                        //Console.WriteLine("Retrieve chan config for module: " + lst[i].ToString() + " , total channels: " + channels);
                        Console.WriteLine("Retrieve agent[{0}] chan config for module: " + lst[i].ToString() + " , total channels: " + channels, agent);
                        for (int j = 0; j < channels; j++)
                        {
                            log.Info("Retrieve channel " + j + " config.");
                            Console.WriteLine("Retrieve channel " + j + " config.");
                            var channel = await _request.Execute<GetChannelConfigRequestModel, ResponseModel<FaxChannel>>(
                                 new GetChannelConfigRequestModel
                                 {
                                     AgentId = agent,
                                     Module = lst[i].ToString().Remove(0, 1),
                                     Channel = j
                                 }, Api.Get.GetFaxConfig, RequestType.GET);
                            //wfClient.GetChannelConfig(agent, lst[i].ToString().Remove(0, 1), j);
                            //FXC3.Entity.Fax.FaxChannel channel = theClient.GetChannelConfig(agent, lst[i].ToString().Remove(0, 1), j);
                            log.Info("Channel " + j + " config retrieved");
                            Console.WriteLine("Channel " + j + " config retrieved");
                            if (channel.Data != null)
                            {
                                _etherFax.ConfigureAnswerRings(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.AnswerRings.Value);
                                _etherFax.ConfigureDialTimeout(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.DialTimeOut.Value);
                                if (cCount < _config.IniChannel)
                                {
                                    _etherFax.ConfigureEnableChan(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.Enabled.Value);
                                    if (channel.Data.Enabled.Value)
                                    {
                                        cCount++;
                                    }
                                    log.Debug("Ini chans stat: " + j.ToString() + "|" + channel.Data.Enabled.ToString() + "|" + cCount.ToString());
                                    Console.WriteLine("Ini chans stat: " + j.ToString() + "|" + channel.Data.Enabled.ToString() + "|" + cCount.ToString());
                                }
                                try
                                {
                                    //prevent exception being thrown and missed the rest of the configs. Auto-recover with default values.
                                    _etherFax.ConfigureHeaderStyle(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.HeaderStyle, channel.Data.HeaderCustomStr);
                                }
                                catch (Exception erRR)
                                {
                                    log.Error("Set Header style error[auto-default]]: " + erRR.Message);
                                    Console.WriteLine("Set Header style error[auto-default]]: " + erRR.Message);
                                    _etherFax.ConfigureHeaderStyle(int.Parse(lst[i].ToString().Remove(0, 1)), j, "DEFAULT", string.Empty);
                                }
                                _etherFax.ConfigureLocalID(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.LocalCSID);
                                _etherFax.ConfigureReceiveEnabled(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.ReceiveEnabled.Value);
                                _etherFax.ConfigureSendEnabled(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.SendEnabled.Value);
                                _etherFax.ConfigureToneDetectDigits(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.ToneDetectDigits.Value);
                                _etherFax.ConfigureToneDetectEnabled(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.ToneDetectEnabled.Value);
                                _etherFax.ConfigureToneWaitTimeout(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.ToneWaitTimeOut.Value);
                                log.DebugFormat("Channel {0} tone wait timeout: {1}", j.ToString(), channel.Data.ToneWaitTimeOut);
                                Console.WriteLine("Channel {0} tone wait timeout: {1}", j.ToString(), channel.Data.ToneWaitTimeOut);
                                try
                                {
                                    _etherFax.ConfigureDTMFToneType(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.GreetingType);
                                }
                                catch (Exception errTT)
                                {
                                    log.Error("set Tone Type Error[auto-default]: " + errTT.Message);
                                    Console.WriteLine("set Tone Type Error[auto-default]: " + errTT.Message);
                                    _etherFax.ConfigureDTMFToneType(int.Parse(lst[i].ToString().Remove(0, 1)), j, "TONE");
                                }
                                _etherFax.ConfigureDTMFToneWav(int.Parse(lst[i].ToString().Remove(0, 1)), j, channel.Data.GreetingWavFile);

                                _etherFax.ConfigureDTMFTerminator(int.Parse(lst[i].ToString().Remove(0, 1)), j, _config.DtmfTerminatorDigit);
                                log.Debug("done channel config: " + j + " channels to go: " + channels);
                                Console.WriteLine("done channel config: " + j + " channels to go: " + channels);
                                _etherFax.ConfigureRecvFormat(int.Parse(lst[i].ToString().Remove(0, 1)), j, rTifFormat);
                            }
                            _etherFax.FaxServerCommit();
                        }
                    }
                }
            }
            //client.Close();
            log.Info("Configuration applied.");
            Console.WriteLine("Configuration applied.");
        }

        /// <summary>
        /// Register new FaxAgent
        /// </summary>
        /// <returns>agent ID</returns>
        private async Task<string> RegisterFaxAgent()
        {
            string agentID = string.Empty;
            string licStat = string.Empty;
            string evalInfo = string.Empty;

            try
            {
                string[] stat = _license.GetLicenseStatus();
                string[] trial = _license.GetTrailPeriod();
                licStat = stat[0].ToString();
                evalInfo = trial[0].ToString();
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent Registration", err.Message, EventLogEntryType.Error, 495);
            }

            log.Info("register fax agent...");
            log.Info("plk: " + _config.Plk);
            log.InfoFormat("licStat: {0}", licStat);
            log.InfoFormat("evalInfo: {0}", evalInfo);

            try
            {
                //calling faxcore fa register web service
                var result = await _request.Execute<RegisterFaxAgentRequestModel, ResponseModel<string>>(
                    new RegisterFaxAgentRequestModel
                    {
                        ServerIp = GetIPAddress(),
                        ServerPort = _config.Port,
                        ServerName = GetServerName(),
                        CountryCode = _config.CountryCode,
                        AreaCode = _config.AreaCode,
                        Plk = _config.Plk,
                        Driver = _config.DriverType,
                        LicStatus = licStat,
                        EvalInfo = evalInfo,
                        IsEFDR = false

                    }, Api.Post.RegisterFaxAgent, RequestType.POST);
                agentID = result.Data;
                log.Info("Agent registered success. AgentID: " + agentID);
            }
            catch (Exception err)
            {
                log.ErrorFormat("FaxAgent register WF error: {0}", err.Message);
                EventLog.WriteEntry("FaxAgent Registration", err.Message, EventLogEntryType.Error, 512);
            }
            return agentID;
        }

        /// <summary>
        /// Register DR FaxAgent
        /// </summary>
        /// <returns>agent ID</returns>
        private async Task<string> RegisterFaxAgentDR()
        {
            //string agentID = string.Empty;
            string licStat = string.Empty;
            string evalInfo = string.Empty;

            try
            {
                string[] stat = _license.GetLicenseStatus();
                string[] trial = _license.GetTrailPeriod();
                licStat = stat[0].ToString();
                evalInfo = trial[0].ToString();
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent DR Registration", err.Message, EventLogEntryType.Error, 897);
            }

            log.Info("register dr fax agent...");
            log.Info("plk: " + _config.Plk);
            log.InfoFormat("licStat: {0}", licStat);
            log.InfoFormat("evalInfo: {0}", evalInfo);
            var efDRAgentID = "";
            try
            {
                //calling faxcore fa register web service
                //require info updates on fa register/ etherfax has special information

                var result = await _request.Execute<RegisterFaxAgentRequestModel, ResponseModel<string>>(
                    new RegisterFaxAgentRequestModel
                    {
                        ServerIp = GetIPAddress(),
                        ServerPort = _config.Port, //"8082",
                        ServerName = GetServerName(),
                        CountryCode = _config.EtherFaxDRCountryCode,
                        AreaCode = _config.EtherFaxDRAreaCode,
                        Plk = _config.Plk,
                        Driver = "EtherFax",
                        LicStatus = licStat,
                        EvalInfo = evalInfo,
                        IsEFDR = true

                    }, Api.Post.RegisterFaxAgent, RequestType.POST);
                efDRAgentID = result.Data;

                log.Info("Agent registered success. AgentID: " + _config.EtherFaxDRAgentID);
            }
            catch (Exception err)
            {
                log.ErrorFormat("DR FaxAgent register WF error: {0}", err.Message);
                EventLog.WriteEntry("DR FaxAgent Registration", err.Message, EventLogEntryType.Error, 512);
            }
            return efDRAgentID;
        }


        #endregion

        #region EventHandlers
        private void WatchOUTDR_Changed(object sender, FileSystemEventArgs e)
        {
            Console.WriteLine("[DR]{2} OUT folder changed. Filename:{0} | Type: {1}", e.Name, e.ChangeType, DateTime.Now);
            log.InfoFormat("[DR]{2} OUT folder changed. Filename:{0} | Type: {1}", e.Name, e.ChangeType, DateTime.Now);

            try
            {
                var t = Task.Factory.StartNew(() => ProcessOutboundXMLDR(e.FullPath)).ContinueWith(d =>
                    {
                        log.InfoFormat("[OUT-DR]: [{0}] processed", e.Name);
                    });
            }
            catch (Exception err)
            {
                log.ErrorFormat("Watch outDR error. File:{0}. Error msg: {1}", e.FullPath, err.Message);
            }
        }

        private void WatchINDR_Changed(object sender, FileSystemEventArgs e)
        {
            Console.WriteLine("[DR]{2} IN folder changed. Filename:{0} | Type: {1}", e.Name, e.ChangeType, DateTime.Now);
            log.InfoFormat("[DR]{2} IN folder changed. Filename:{0} | Type: {1}", e.Name, e.ChangeType, DateTime.Now);

            try
            {
                var t = Task.Factory.StartNew(() => ProcessInboundXMLDR(e.FullPath)).ContinueWith(d =>
                    {
                        log.InfoFormat("[IN-DR]: [{0}] processed", e.Name);
                    });
            }
            catch (Exception err)
            {
                log.ErrorFormat("Watch inDR error. File:{0}. Error msg: {1}", e.FullPath, err.Message);
            }
        }

        private void ProcessInboundXMLDR(string FullPath)
        {
            try
            {
                string dateFld = DateTime.Today.ToString("yyyyMMdd");
                //FXC2.BL.Objects.FaxJobIn inbound = new FXC2.BL.Objects.FaxJobIn();
                FaxJobIn inbound = new FaxJobIn();
                FaxJobIn inJob = new FaxJobIn();  // global::FaxAgent.FXCWS.FaxJobIn();
                                                  //FXC3.Entity.Job.FaxJobIn inJob = new FXC3.Entity.Job.FaxJobIn();
                log.Debug("begin read FWS: " + FullPath);
                FileStream rdr = new FileStream(FullPath, FileMode.Open, FileAccess.Read);
                log.Debug("complete read FWS: " + FullPath);
                XmlReader xmlReader = new XmlTextReader(rdr);

                try
                {
                    System.Xml.Serialization.XmlSerializer de = new System.Xml.Serialization.XmlSerializer(typeof(FaxJobIn));

                    FileStream fs;
                    Byte[] tif;
                    int len = 0;
                    inbound = (FaxJobIn)de.Deserialize(xmlReader);
                    Console.WriteLine("[DR]looking for received file in {0}", inbound.ReceiveFile);
                    if (File.Exists(inbound.ReceiveFile))
                    {
                        fs = new FileStream(inbound.ReceiveFile, FileMode.Open, FileAccess.Read);
                        tif = new Byte[fs.Length];
                        len = (int)fs.Length;
                        fs.Read(tif, 0, len);
                        fs.Close();
                        fs.Dispose();
                    }
                    else
                    {
                        tif = new Byte[0];
                    }
                    log.Info("csid from xml: " + inbound.RemoteId);

                    inJob.CalledNumber = inbound.CalledNumber;
                    inJob.CallingNumber = inbound.CallingNumber;
                    inJob.Channel = inbound.Channel;
                    inJob.ConnectSpeed = inbound.ConnectSpeed;
                    inJob.ConnectTime = inbound.ConnectTime;
                    inJob.FaxEncoding = inbound.FaxEncoding;
                    inJob.InFaxResult = inbound.InFaxResult;
                    log.Debug("page count on receive: " + inbound.PagesReceived.ToString());
                    if (inbound.PagesReceived < 1)
                    {
                        inJob.InFaxResult = "FileError";
                    }
                    inJob.PagesReceived = inbound.PagesReceived;
                    inJob.ReceiveFile = inbound.ReceiveFile;
                    inJob.RemoteId = ParseCSID(inbound.RemoteId); //updated 29/05/2013 Ticket 11612
                    inJob.RoutingInfo = inbound.RoutingInfo;
                    inJob.BarcodeCount = inbound.BarcodeCount;
                    inJob.DateIn = inbound.DateIn;
                    inJob.ReferredID = inbound.ReferredID;  //OCS integration Referred-ID
                    if (inbound.BarcodeCount > 0)
                    {
                        inJob.BarcodeResultFile = inbound.BarcodeResultFile;

                        //retrieve barcode XML values 
                        if (File.Exists(inbound.BarcodeResultFile))
                        {
                            string xml = string.Empty;
                            using (StreamReader rdrXML = new StreamReader(inbound.BarcodeResultFile))
                            {
                                xml = rdrXML.ReadToEnd();
                            }
                            inJob.BarcodeXML = xml;
                            Helper.DeleteFile(inbound.BarcodeResultFile);
                        }
                    }
                    if (inJob.CalledNumber == null)
                    {
                        inJob.CalledNumber = string.Empty;
                    }
                    //console tag
                    Console.WriteLine("====DR Inbound Progress Begin=====");
                    Console.WriteLine("Channel: {0}", inbound.Channel);
                    Console.WriteLine("Connect speed: {0}", inbound.ConnectSpeed);
                    Console.WriteLine("Connect time: {0}", inbound.ConnectTime);
                    Console.WriteLine("Fax encoding: {0}", inbound.FaxEncoding);
                    Console.WriteLine("Result: {0}", inbound.InFaxResult);
                    Console.WriteLine("Pages received: {0}", inbound.PagesReceived);
                    Console.WriteLine("Remote ID: {0}", inbound.RemoteId);
                    Console.WriteLine("Called number: {0}", inbound.CalledNumber);
                    Console.WriteLine("Calling number: {0}", inbound.CallingNumber);
                    Console.WriteLine("Date Out: {0}", inbound.DateIn);
                    Console.WriteLine("Routing info: {0}", inbound.RoutingInfo);
                    Console.WriteLine("Barcode count: {0}", inbound.BarcodeCount.ToString());
                    Console.WriteLine("Referred ID: {0}", inbound.ReferredID);
                    //_procEfChannel[inbound.Channel].CheckChannel();

                    if (!String.IsNullOrEmpty(inbound.BarcodeResultFile))
                    {
                        if (File.Exists(inJob.BarcodeResultFile))
                        {
                            MoveTif(inJob.BarcodeResultFile, AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\archive\" + dateFld + "\\in");
                        }
                    }
                    log.Debug(inJob.BarcodeXML);
                    if (true) //OpenWCFConnection())
                    {
                        //incoming.Open();
                        //theClient.Incoming(inJob, fa.GetFSIPAddress(), efDRAgentID, tif);
                        _request.Execute<IncomingRequestModel, HttpResponseDetail>(
                            new IncomingRequestModel
                            {
                                InJob = inJob,
                                Agentid = _config.EtherFaxDRAgentID,
                                ServerIp = _etherFax.GetFSIPAddress(),
                                Tiff = tif
                            }, Api.Post.Incoming, RequestType.POST).GetAwaiter().GetResult();
                        //incoming.Incoming(inJob, fa.GetFSIPAddress(), efDRAgentID, tif);
                        //incoming.Close(); 
                    }
                    rdr.Close();
                    Console.WriteLine("[DR] update incoming fax to server");
                    if (File.Exists(inJob.ReceiveFile))
                    {
                        Console.WriteLine("[DR]move receive file to archive.");
                        MoveTif(inJob.ReceiveFile, AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\archive\" + dateFld + "\\in");
                    }
                    Console.WriteLine("[DR]move xml to archive");
                    MoveTif(FullPath, AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\archive\" + dateFld + "\\in");

                    Console.WriteLine("=====DR Inbound Progress End======");
                }
                catch (Exception err)
                {
                    EventLog.WriteEntry("Incoming FaxDR", err.Message, EventLogEntryType.Error, 1798);
                }
                finally
                {
                    rdr.Close();
                    xmlReader.Close();
                    _procEfChannel[inJob.Channel].CheckChannel(false);
                }
            }
            catch (FileNotFoundException ioErr)
            {
                log.Error("FWS IN: " + ioErr.Message);
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "Error In FWS IN: " + err.ToString(), EventLogEntryType.Error, 1778);
            }
        }

        private void ProcessOutboundXMLDR(string FullPath)
        {
            try
            {
                string dateFld = DateTime.Today.ToString("yyyyMMdd");
                //FXC3.Entity.Job.FaxJobOut outJob = new FXC3.Entity.Job.FaxJobOut();
                FaxJobOut outJob = new FaxJobOut(); // global::FaxAgent.FXCWS.FaxJobOut();
                FaxJobOut outbound = new FaxJobOut();
                //FXC2.BL.Objects.FaxJobOut outbound = new FXC2.BL.Objects.FaxJobOut();
                bool result = false;
                //log.Debug("Out watch: " + e.Name);
                //System.Threading.Thread.Sleep(100);
                FileStream fs = new FileStream(FullPath, FileMode.Open);
                XmlReader xmlReader = new XmlTextReader(fs);
                try
                {
                    System.Xml.Serialization.XmlSerializer de = new System.Xml.Serialization.XmlSerializer(typeof(FaxJobOut));

                    outbound = (FaxJobOut)de.Deserialize(xmlReader);
                    outJob.Channel = outbound.Channel;
                    outJob.ConnectSpeed = outbound.ConnectSpeed;
                    outJob.ConnectTime = outbound.ConnectTime;
                    outJob.FaxEncoding = outbound.FaxEncoding;
                    outJob.OutFaxResult = outbound.OutFaxResult;
                    outJob.PagesDelivered = outbound.PagesDelivered;
                    outJob.RemoteId = ParseCSID(outbound.RemoteId);  //updated 29/05/2013 Ticket 11612
                    outJob.TAG = outbound.TAG;
                    outJob.ExtendedResult = outbound.ExtendedResult;
                    outJob.DateOut = outbound.DateOut;

                    //console tag
                    Console.WriteLine("====DR Outbound Progress Begin=====");
                    Console.WriteLine("Channel: {0}", outbound.Channel);
                    Console.WriteLine("Connect speed: {0}", outbound.ConnectSpeed);
                    Console.WriteLine("Connect time: {0}", outbound.ConnectTime);
                    Console.WriteLine("Fax encoding: {0}", outbound.FaxEncoding);
                    Console.WriteLine("Result: {0}", outbound.OutFaxResult);
                    Console.WriteLine("Pages delivered: {0}", outbound.PagesDelivered);
                    Console.WriteLine("Remote ID: {0}", outbound.RemoteId);
                    Console.WriteLine("TAG: {0}", outbound.TAG.ToString());
                    Console.WriteLine("Extended Result: {0}", outbound.ExtendedResult);
                    Console.WriteLine("Date Out: {0}", outbound.DateOut);
                    //_procEfChannel[outbound.Channel].CheckChannel();

                    if (outbound.TAG != null)
                    {
                         _request.Execute<OutboundRequestModel, ResponseModel<bool>>(
                                new OutboundRequestModel
                                {
                                    OutJob = outJob,
                                    ServerIp = _etherFax.GetFSIPAddress(),
                                    AgentId = _config.EtherFaxDRAgentID
                                }, Api.Post.Outbound, RequestType.POST).GetAwaiter().GetResult();
                        fs.Close();
                        log.Info("out watch result: " + result.ToString() + " complete #" + outJob.Channel);
                        Console.WriteLine("[DR] Update job to server.");
                        MoveTif(FullPath, AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\archive\" + dateFld + "\\out");
                        Console.WriteLine("[DR]move tif to archive folder.");
                    }
                    else
                    {
                        fs.Close();
                        MoveTif(FullPath, AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\archive\" + dateFld + "\\out\\Debug");
                    }
                    Console.WriteLine("=====DR Outbound Progress End======");
                }
                catch (Exception err)
                {
                    EventLog.WriteEntry("Outgoing Fax DR", err.Message, EventLogEntryType.Error, 1783);
                }
                finally
                {
                    fs.Close();
                    xmlReader.Close();
                    _procEfChannel[outJob.Channel].CheckChannel(false);
                }
                //MoveTif(e.FullPath, AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive\" + dateFld + "\\out");
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("WatchOut DR", err.Message, EventLogEntryType.Error, 1784);
            }
        }

        private void TimeStatusDR_Elapsed(object sender, ElapsedEventArgs e)
        {
            UpdateChannelStatusDR();

            string[] licStat = _license.GetLicenseStatus();
            string[] evalInfo = _license.GetTrailPeriod();

            if (licStat[0].ToString().ToUpper().Contains("EVALUATION-EXPIRED") && licStat[1].ToString().ToUpper().Equals("FALSE"))
            {
                try
                {
                    ArrayList chanCount = _etherFax.GetChannelCount();
                    for (int i = 0; i < chanCount.Count; i++)
                    {
                        _etherFax.ConfigureEnableChan(int.Parse(chanCount[i].ToString()), false);
                    }
                }
                catch (Exception err)
                {
                    EventLog.WriteEntry("FaxAgentDR TS Status Update", err.Message, EventLogEntryType.Warning, 1751);
                }
            }
        }

        private void WatchOut_Changed(object sender, FileSystemEventArgs e)
        {
            Console.WriteLine("[OUT]{2} folder changed. Filename:{0} | Type: {1}", e.Name, e.ChangeType, DateTime.Now);
            log.InfoFormat("[OUT]{2} folder changed. Filename:{0} | Type: {1}", e.Name, e.ChangeType, DateTime.Now);

            try
            {
                var t = Task.Factory.StartNew(() => ProcessOutboundXML(e.FullPath)).ContinueWith(d =>
                    {
                        log.InfoFormat("[OUT]: [{0}] processed", e.Name);
                    });
            }
            catch (Exception err)
            {
                log.ErrorFormat("Watch out error. File:{0}. Error msg: {1}", e.FullPath, err.Message);
            }
        }

        private void ProcessOutboundXML(string FullPath)
        {
            try
            {
                string dateFld = DateTime.Today.ToString("yyyyMMdd");
                //FXC3.Entity.Job.FaxJobOut outJob = new FXC3.Entity.Job.FaxJobOut();
                FaxJobOut outJob = new FaxJobOut(); // global::FaxAgent.FXCWS.FaxJobOut();
                FaxJobOut outbound = new FaxJobOut();
                //FXC2.BL.Objects.FaxJobOut outbound = new FXC2.BL.Objects.FaxJobOut();
                //bool result = false;
                //log.Debug("Out watch: " + e.Name);
                //System.Threading.Thread.Sleep(100);
                FileStream fs = new FileStream(FullPath, FileMode.Open);
                XmlReader xmlReader = new XmlTextReader(fs);
                try
                {
                    System.Xml.Serialization.XmlSerializer de = new System.Xml.Serialization.XmlSerializer(typeof(FaxJobOut));

                    outbound = (FaxJobOut)de.Deserialize(xmlReader);
                    outJob.Channel = outbound.Channel;
                    outJob.ConnectSpeed = outbound.ConnectSpeed;
                    outJob.ConnectTime = outbound.ConnectTime;
                    outJob.FaxEncoding = outbound.FaxEncoding;
                    outJob.OutFaxResult = outbound.OutFaxResult;
                    outJob.PagesDelivered = outbound.PagesDelivered;
                    outJob.RemoteId = ParseCSID(outbound.RemoteId); //updated 29/05/2013 Ticket 11612
                    outJob.TAG = outbound.TAG;
                    outJob.ExtendedResult = outbound.ExtendedResult;
                    outJob.DateOut = outbound.DateOut;

                    //for xcapi falsely reporting outbound result
                    if (outbound.OutFaxResult.ToLower() =="ok" && outbound.PagesDelivered==0)
                    {
                        outJob.OutFaxResult="Error";
                        outJob.ExtendedResult = 44;//todo : replace with a proper error
                    }

                    //console tag
                    Console.WriteLine("====Outbound Progress Begin=====");
                    Console.WriteLine("Channel: {0}", outbound.Channel);
                    Console.WriteLine("Connect speed: {0}", outbound.ConnectSpeed);
                    Console.WriteLine("Connect time: {0}", outbound.ConnectTime);
                    Console.WriteLine("Fax encoding: {0}", outbound.FaxEncoding);
                    Console.WriteLine("Result: {0}", outbound.OutFaxResult);
                    Console.WriteLine("Pages delivered: {0}", outbound.PagesDelivered);
                    Console.WriteLine("Remote ID: {0}", outbound.RemoteId);
                    Console.WriteLine("TAG: {0}", outbound.TAG.ToString());
                    Console.WriteLine("Extended Result: {0}", outbound.ExtendedResult);
                    Console.WriteLine("Date Out: {0}", outbound.DateOut);
#if DEBUG
                    log.DebugFormat("call for check channel {0}", outbound.Channel);
#endif
                    //_procChannel[outbound.Channel].CheckChannel();
                    if (outbound.TAG != null)
                    {
                        var response =  _request.Execute<OutboundRequestModel, ResponseModel<bool>>(
                                new OutboundRequestModel
                                {
                                    OutJob = outJob,
                                    ServerIp = _faxServer.GetFSIPAddress(),
                                    AgentId = _config.AgentId
                                }, Api.Post.Outbound, RequestType.POST).GetAwaiter().GetResult();

                        fs.Close();
                        log.Info("out watch result: " + response.Data.ToString() + " complete #" + outJob.Channel);
                        Console.WriteLine("Update job to server.");
                        MoveTif(FullPath, AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive\" + dateFld + "\\out");
                        Console.WriteLine("move tif to archive folder.");
                    }
                    else
                    {
                        fs.Close();
                        MoveTif(FullPath, AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive\" + dateFld + "\\out\\Debug");
                    }
                    Console.WriteLine("=====Outbound Progress End======");
                }
                catch (Exception err)
                {
                    EventLog.WriteEntry("Outgoing Fax", err.Message, EventLogEntryType.Error, 74);
                }
                finally
                {
                    fs.Close();
                    xmlReader.Close();
                    _procChannel[outJob.Channel].CheckChannel(false);
                }
                //MoveTif(e.FullPath, AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive\" + dateFld + "\\out");
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("WatchOut", err.Message, EventLogEntryType.Error, 67);
            }
        }


        private void WatchIn_Changed(object sender, FileSystemEventArgs e)
        {
            Console.WriteLine("[IN]{2} folder changed. Filename:{0} | Type: {1}", e.Name, e.ChangeType, DateTime.Now);
            log.InfoFormat("[IN]{2} folder changed. Filename:{0} | Type: {1}", e.Name, e.ChangeType, DateTime.Now);

            try
            {
                if (File.Exists(e.FullPath))
                {
                    File.Move(e.FullPath, Path.Combine(Path.GetDirectoryName(e.FullPath), "temp", e.Name));

                    var t = Task.Factory.StartNew(() => ProcessInboundXML(Path.Combine(Path.GetDirectoryName(e.FullPath), "temp", e.Name))).ContinueWith(d =>
                    {
                        log.InfoFormat("[IN]: [{0}] processed", e.Name);
                    });
                }
            }
            catch (Exception err)
            {
                log.ErrorFormat("Watch in error. File:{0}. Error msg: {1}", e.FullPath, err.Message);
            }
        }

        /// <summary>
        /// process inbound fax in async task threads
        /// </summary>
        /// <param name="FullPath">xml full path</param>
        private void ProcessInboundXML(string FullPath)
        {
            bool isWCFOpen = false;
            try
            {
                int waitCount = 3;
                string dateFld = DateTime.Today.ToString("yyyyMMdd");
                //FXC2.BL.Objects.FaxJobIn inbound = new FXC2.BL.Objects.FaxJobIn();
                FaxJobIn inbound = new FaxJobIn();
                FaxJobIn inJob = new FaxJobIn();

                //file wait
                for (int i = 0; i < waitCount; i++)
                {
                    if (IsFileReady(FullPath))
                    {
                        i = waitCount;
                    }
                    else
                    {
                        Console.WriteLine("File {0} not ready, waiting...{1}", FullPath, i + 1);
                        System.Threading.Thread.Sleep(500);
                    }
                }

                //log.Debug("begin read FWS: " + e.FullPath);
                FileStream rdr = new FileStream(FullPath, FileMode.Open, FileAccess.Read);
                //log.Debug("complete read FWS: " + e.FullPath);
                XmlReader xmlReader = new XmlTextReader(rdr);

                try
                {
                    System.Xml.Serialization.XmlSerializer de = new System.Xml.Serialization.XmlSerializer(typeof(FaxJobIn));

                    FileStream fs;
                    Byte[] tif;
                    int len = 0;
                    inbound = (FaxJobIn)de.Deserialize(xmlReader);
                    //wfFaxAgentClient incoming = new wfFaxAgentClient(httpBinding, endPoint);
                    Console.WriteLine("[IB]looking for received file in {0}", inbound.ReceiveFile);
                    if (File.Exists(inbound.ReceiveFile))
                    {
                        fs = new FileStream(inbound.ReceiveFile, FileMode.Open, FileAccess.Read);
                        tif = new Byte[fs.Length];
                        len = (int)fs.Length;
                        fs.Read(tif, 0, len);
                        fs.Close();
                        fs.Dispose();
                    }
                    else
                    {
                        tif = new Byte[0];
                    }
                    log.Info("[IB]csid from xml: " + inbound.RemoteId);

                    inJob.CalledNumber = inbound.CalledNumber;
                    inJob.CallingNumber = inbound.CallingNumber;
                    inJob.Channel = inbound.Channel;
                    inJob.ConnectSpeed = inbound.ConnectSpeed;
                    inJob.ConnectTime = inbound.ConnectTime;
                    inJob.FaxEncoding = inbound.FaxEncoding;
                    inJob.InFaxResult = inbound.InFaxResult;
                    log.Debug("[IB]page count on receive: " + inbound.PagesReceived.ToString());
                    if (inbound.PagesReceived < 1)
                    {
                        inJob.InFaxResult = "FileError";
                    }
                    inJob.PagesReceived = inbound.PagesReceived;
                    inJob.ReceiveFile = inbound.ReceiveFile;
                    inJob.RemoteId = ParseCSID(inbound.RemoteId); //updated 29/05/2013 Ticket 11612
                    inJob.RoutingInfo = inbound.RoutingInfo;
                    inJob.BarcodeCount = inbound.BarcodeCount;
                    inJob.DateIn = inbound.DateIn;
                    inJob.ReferredID = inbound.ReferredID;  //OCS integration Referred-ID
                    if (inbound.BarcodeCount > 0)
                    {
                        inJob.BarcodeResultFile = inbound.BarcodeResultFile;

                        //retrieve barcode XML values 
                        if (File.Exists(inbound.BarcodeResultFile))
                        {
                            string xml = string.Empty;
                            using (StreamReader rdrXML = new StreamReader(inbound.BarcodeResultFile))
                            {
                                xml = rdrXML.ReadToEnd();
                            }
                            inJob.BarcodeXML = xml;
                            Helper.DeleteFile(inbound.BarcodeResultFile);
                        }
                    }
                    if (inJob.CalledNumber == null)
                    {
                        inJob.CalledNumber = string.Empty;
                    }

                    //console tag
                    Console.WriteLine("====Inbound Progress Begin=====");
                    Console.WriteLine("Channel: {0}", inbound.Channel);
                    Console.WriteLine("Connect speed: {0}", inbound.ConnectSpeed);
                    Console.WriteLine("Connect time: {0}", inbound.ConnectTime);
                    Console.WriteLine("Fax encoding: {0}", inbound.FaxEncoding);
                    Console.WriteLine("Result: {0}", inbound.InFaxResult);
                    Console.WriteLine("Pages received: {0}", inbound.PagesReceived);
                    Console.WriteLine("Remote ID: {0}", inbound.RemoteId);
                    Console.WriteLine("Called number: {0}", inbound.CalledNumber);
                    Console.WriteLine("Calling number: {0}", inbound.CallingNumber);
                    Console.WriteLine("Date Out: {0}", inbound.DateIn);
                    Console.WriteLine("Routing info: {0}", inbound.RoutingInfo);
                    Console.WriteLine("Barcode count: {0}", inbound.BarcodeCount.ToString());
                    Console.WriteLine("Referred ID: {0}", inbound.ReferredID);
#if DEBUG
                    log.DebugFormat("call for check channel {0}", inbound.Channel);
#endif
          //_procChannel[inbound.Channel].CheckChannel() ;
          if (!String.IsNullOrEmpty(inbound.BarcodeResultFile))
          {
            if (File.Exists(inJob.BarcodeResultFile))
            {
              MoveTif(inJob.BarcodeResultFile, AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive\" + dateFld + "\\in");
            }
          }
          log.Debug(inJob.BarcodeXML);
          //incoming.Open();
          isWCFOpen = true; // OpenWCFConnection();
          //log.DebugFormat("WCF Open State: {0}", isWCFOpen);
          if (isWCFOpen)
          {
            Console.WriteLine("Local IP:{0} | AgentID:{1}", _faxServer.GetFSIPAddress(), _config.AgentId);
            //bool inComp = theClient.Incoming(inJob, fa.GetFSIPAddress(), agentID, tif);
            if (inbound.ConnectSpeed > 0 && inbound.PagesReceived > 0)
            {
              var inComp =  _request.Execute<IncomingRequestModel, ResponseModel<bool>>(
              new IncomingRequestModel
              {
                InJob = inJob,
                Agentid = _config.AgentId,
                ServerIp = _faxServer.GetFSIPAddress(),
                Tiff = tif
              }, Api.Post.Incoming, RequestType.POST).GetAwaiter().GetResult();
              log.InfoFormat("InJob {0} -size {3}- pushed to agent {1} : Result: {2}", inJob.ReceiveFile, _config.AgentId, inComp.Data.ToString(), tif.Length);
              Console.WriteLine("fax pushed to server: " + inComp.Data.ToString());
            }
          }
          //incoming.Close();
          rdr.Close();
          Console.WriteLine("update incoming fax to server");
          if (isWCFOpen)
          {
            if (File.Exists(inJob.ReceiveFile))
            {
              Console.WriteLine("move receive file to archive.");
              MoveTif(inJob.ReceiveFile, AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive\" + dateFld + "\\in");
            }
            Console.WriteLine("move xml to archive");
            MoveTif(FullPath, AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive\" + dateFld + "\\in");

                    }
                    Console.WriteLine("=====Inbound Progress End======");
                }
                catch (Exception err)
                {
                    EventLog.WriteEntry("Incoming Fax", err.Message, EventLogEntryType.Error, 82);
                }
                finally
                {
                    rdr.Close();
                    xmlReader.Close();
                    _procChannel[inJob.Channel].CheckChannel(false) ;
                }
            }
            catch (FileNotFoundException ioErr)
            {
                log.Error("FWS IN: " + ioErr.Message);
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "Error In FWS IN: " + err.ToString(), EventLogEntryType.Error, 474);
            }
        }

        private void TimeFaxTimeout_Elapsed(object sender, ElapsedEventArgs e)
        {
            lock (_timeFaxTimeout)
            {
                if (!string.IsNullOrEmpty(_config.AgentId))
                {
                    ResetQueue(_config.AgentId, 15);//_config.FaxTimeout);  //reschedule queue item if sit long than 15 min         
                }          


                if (!string.IsNullOrEmpty(_config.EtherFaxDRAgentID))
                {
                    ResetQueue(_config.EtherFaxDRAgentID, 15); //reschedule queue item for DR.
                }
                
            }
        }

        private void TimeStatus_Elapsed(object sender, ElapsedEventArgs e)
        {
            UpdateChannelStatus();

            string[] licStat = _license.GetLicenseStatus();
            string[] evalInfo = _license.GetTrailPeriod();

            if (licStat[0].ToString().ToUpper().Contains("EVALUATION-EXPIRED") && licStat[1].ToString().ToUpper().Equals("FALSE"))
            {
                try
                {
                    ArrayList chanCount = _faxServer.GetChannelCount();
                    for (int i = 0; i < chanCount.Count; i++)
                    {
                        _faxServer.ConfigureEnableChan(int.Parse(chanCount[i].ToString()), false);
                    }
                }
                catch (Exception err)
                {
                    EventLog.WriteEntry("FaxAgent TS Status Update", err.Message, EventLogEntryType.Warning, 1668);
                }
            }
        }

        private void TimeQPick_Elapsed(object sender, ElapsedEventArgs e)
        {
            _timeQPick.Stop();

            try
            {
            }
            catch (Exception err)
            {
                log.Error("qpick time error: " + err.Message);
            }
            finally
            {
                _timeQPick.Interval = 5000;
                Thread.Sleep(5000);
                _timeQPick.Start();
            }
        }

        private void TimePool_Elapsed(object sender, ElapsedEventArgs e)
        {
            lock (_timePool)
            {
                PollingRPC();
            }
        }
        #endregion


        #region PubSub
        public void StartSubscriber()
        {
            try
            {
                using (SubscriberSocket subscriber = new SubscriberSocket())
                {
                    string subscriberChannel = ConfigurationManager.AppSettings.Get("PubSubSocketChannel");
                    subscriberChannel = string.IsNullOrWhiteSpace(subscriberChannel) ? "tcp://localhost:2379" : subscriberChannel;
                    subscriber.Connect(subscriberChannel);
                    subscriber.Subscribe("FaxAgent");
                    log.InfoFormat("Sub to channel {0}, endpoint: {1}","FaxAgent", subscriber.Options.LastEndpoint);
                    while (true)
                    {
                        Console.WriteLine("Checking message");
                        var topic = subscriber.ReceiveFrameString();
                        var msg = subscriber.ReceiveFrameString();
                        Console.WriteLine("[PubSubSocketSubscriber] From Publisher: {0} {1}", topic, msg);
                        log.InfoFormat("[PS]Topic:{0}, MSG:{1}", topic, msg);

                        if (!string.IsNullOrEmpty(msg))
                        {
                            string[] jobData = msg.Split(new char[] { '|'});
                            //assign job
                            Console.WriteLine("Job for AgentID: {0}", jobData[0]);
                            if (jobData[0] == _config.AgentId)
                            {
                                Parallel.Invoke(() => AssignJob(msg, false));
                            }
                            else if (jobData[0] == _config.EtherFaxDRAgentID)
                            {
                                Parallel.Invoke(() => AssignJob(msg, true));
                            }
                        }
                        //CheckChannel(false);
                        //Thread.Sleep(1500);
                    }
                }
            }
            catch (Exception m)
            {
                Console.WriteLine("[PubSubSocketSubscriber] Error: {0}", m.Message);
                log.ErrorFormat("[PubSubSocketSubscriber] Error: {0}", m.Message);
            }
        }
        #endregion


        #region Initializing WebApi Selfhost

        private void StartWebApiServer()
        {
            if (_webHostApi != null)
                return;

            var baseAddress = _config.HostUrl; //ConfigurationManager.AppSettings.Get("hosturl");
            var startup = new Startup();
            _webHostApi = WebApp.Start(baseAddress, startup.Configure);
            _container = startup.Container;
            _faxServer = _container.Resolve<FaxServerHostContext>();
            _etherFax = _container.Resolve<EtherFaxHostContext>();
        }
        private bool _inital = true;
        private const int MAX_RETRIES = int.MaxValue;
        private void Authenticate()
        {
            var request = new AuthClientCredentialModel
            {
                ClientId = ConfigurationManager.AppSettings.Get("api:client_id"),
                ClientSecret = ConfigurationManager.AppSettings.Get("api:client_secret")
            };


            var retryPolicy = Policy.Handle<Exception>()
                .WaitAndRetry(retryCount: MAX_RETRIES, sleepDurationProvider: (attemptCount) =>
                {
                    return (attemptCount < 60)
                        ? TimeSpan.FromSeconds(30)
                        : TimeSpan.FromSeconds(attemptCount * 2);
                },
                onRetry: (exception, sleepDuration, attemptNumber, context) =>
                {
                    var error = $"Fault: {exception.Message}. Retrying in {sleepDuration}. {attemptNumber} / {MAX_RETRIES}";
                    log.ErrorFormat(error);
                    Console.WriteLine(error);
                });

            retryPolicy.Execute(() =>
            {
                var task = AuthenticationFactory.Instance
                       .CreateSession(request, false)
                       .GetAwaiter()
                       .GetResult();
                if (task.IsSuccess() && AuthContextSession.Instance.HasToken)
                {
                    Console.WriteLine("-----------------------------------------------");
                    log.DebugFormat($"New token acquired: {AuthContextSession.Instance.Token.Token}");
                    Console.WriteLine($"New token acquired: {AuthContextSession.Instance.Token.Token}");
                    Console.WriteLine("-----------------------------------------------");

                    if (_inital)
                    {
                        FaxAgentTimeInit();
                        FaxAgentSvcStart();
                        _inital = false;
                    }
                }
                else
                    throw new Exception("Unable to connect to OAuth Server");
            });
        }

        private void Instance_TokenExpired(object sender, EventArgs e)
        {
            try
            {
                _timePool.Enabled = false;
                _timeQPick.Enabled = false;
                _timeStatus.Enabled = false;
                _watchIn.EnableRaisingEvents = false;
                _watchOut.EnableRaisingEvents = false;
                _watchINDR.EnableRaisingEvents = false;
                _watchOUTDR.EnableRaisingEvents = false;
            }
            catch (Exception err)
            {
                Console.WriteLine("Unable to stop timer due to obj not initialized.");
            }

            Authenticate();

            _timePool.Enabled = true;
            _timeQPick.Enabled = true;
            _timeStatus.Enabled = true;
            _watchIn.EnableRaisingEvents = true;
            _watchOut.EnableRaisingEvents = true;
            _watchINDR.EnableRaisingEvents = true;
            _watchOUTDR.EnableRaisingEvents = true;
        }

        private void Instance_TokenSet(object sender, EventArgs e)
        {
            log.Debug("Token Setup");
        }

        #endregion
    }
}