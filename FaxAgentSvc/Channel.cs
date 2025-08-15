namespace FaxAgentSvc
{
    using Dapper;
    using FaxAgentSvc.Context;
    using FXC.ServiceAgent;
    using FXC6.Entity.Api;
    using FXC6.Entity.Job;
    using log4net;
    using log4net.Config;
    using NetMQ;
    using NetMQ.Sockets;
    using Newtonsoft.Json;
    using System;
    using System.Configuration;
    using System.Data.SqlClient;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    public delegate void PickJob(int channel);
    public class Channel
    {
        //public event PickJob PickTheFreakingJob;
        private readonly RequestClient _request = new RequestClient();
        private static ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static string connString = string.Empty;//ConfigurationManager.ConnectionStrings["FaxCoreConnect"].ConnectionString;
        private string moduleList = string.Empty;
        private double timerInterval = 5000;
        private string agentID = string.Empty;
        private bool isEFDR = false;
        private bool sqlDependencyError = false;
        private static bool serverStatus = true;
        private string _server = string.Empty;
        private int _channelDelay = 0;
        private EtherFaxHostContext faxAgent;
        private FaxServerHostContext faxAgentFA;
        //System.Timers.Timer timer;

        //backup timer to jumpstart dead sql notification
        private double timerBackInterval = new TimeSpan(0, 5, 0).TotalMilliseconds;
        //System.Timers.Timer timerBackup;
        bool _useTimer = false;
        int rowCount = 0; //counter for channel queue

        private TaskCompletionSource<object> _timerTcs;
        private Func<object> _timerFunc;

        public string ModuleChannel
        {
            set { moduleList = value; }
            get { return moduleList; }
        }

        public double Interval
        {
            set { timerInterval = value; }
        }

        public string DriverType { get; set; } = string.Empty;

        public void EnableTimer()
        {
            // Disable Timer as pub sub socket replaced it
            //_timerTcs = _timerFunc.StartNewTimer(TimeSpan.FromMilliseconds(timerInterval), log);

            //this.timer.Interval = timerInterval;

            //timerBackInterval = timerInterval;
            //this.timerBackup.Interval = timerInterval;
            //this.timerBackup.Enabled = true;
            //_useTimer = true;
        }

        public void StartSubscriber() 
        {
            try
            {
                using (SubscriberSocket subscriber = new SubscriberSocket())
                {
                    string subscriberChannel = ConfigurationManager.AppSettings.Get("PubSubSocketChannel");
                    subscriberChannel = string.IsNullOrWhiteSpace(subscriberChannel) ? "tcp://*:2378" : subscriberChannel;
                    subscriber.Connect(subscriberChannel);
                    subscriber.Subscribe("Trigger");

                    while (true)
                    {
                        Console.WriteLine("Checking message");
                        var topic = subscriber.ReceiveFrameString();
                        var msg = subscriber.ReceiveFrameString();
                        Console.WriteLine("[PubSubSocketSubscriber] From Publisher: {0} {1}", topic, msg);

                        CheckChannel(false);
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

        public void StartMonitorQueue()
        {
            this.MonitorQueueChannel(agentID)
                .GetAwaiter()
                .GetResult();
        }

        public void DisableTimer()
        {
            if (_timerTcs != null)
                _timerTcs.SetCanceled(); 
            //this.timer.Enabled = false;
        }

        public bool ServerStatus
        {
            set { serverStatus = value; }
            get { return serverStatus; }
        }

        public string Server
        {
            set { _server = value; }
            get { return _server; }
        }

        public int ChannelDelay
        {
            set { _channelDelay = value; }
            get { return _channelDelay; }
        }

        public Channel(EtherFaxHostContext ef, string AgentID, bool isDR)
        {
            XmlConfigurator.Configure();
            _timerFunc = new Func<object>(() =>
            {
                //log.DebugFormat("[CCTimer-{0}] Kicks in!", moduleList);
                CheckChannel(false);
                return null;
            });

            //timer = new System.Timers.Timer();
            //timer.Interval = timerInterval;
            //timer.Elapsed += new System.Timers.ElapsedEventHandler(timer_Elapsed);
            //timer.Enabled = false;
            faxAgent = ef;
            agentID = AgentID;
            isEFDR = isDR;
            log.DebugFormat("[CCDR]Channel obj created for agent {0} | Module: {1}", agentID, moduleList);

            //timerBackup = new System.Timers.Timer();
            //timerBackup.Interval = timerBackInterval;
            //timerBackup.Elapsed += new System.Timers.ElapsedEventHandler(timerBackup_Elapsed);
        }

        public Channel(FaxServerHostContext fa, string AgentID)
        {
            XmlConfigurator.Configure();
            _timerFunc = new Func<object>(() =>
            {
                //log.DebugFormat("[CCTimer-{0}] Kicks in!", moduleList);
                CheckChannel(false);
                return null;
            });
            //timer = new System.Timers.Timer();
            //timer.Interval = timerInterval;
            //timer.Elapsed += new System.Timers.ElapsedEventHandler(timer_Elapsed);
            //timer.Enabled = false; //suppose to run on start when monitorqueue is executed
            faxAgentFA = fa;
            agentID = AgentID;
            log.DebugFormat("[CC]Channel obj created for agent {0} | Module: {1}", agentID, moduleList);

            //timerBackup = new System.Timers.Timer();
            //timerBackup.Interval = timerBackInterval;
            //timerBackup.Elapsed += new System.Timers.ElapsedEventHandler(timerBackup_Elapsed);
            //timerBackup.Enabled = true;
            //PickTheFreakingJob += Channel_PickTheFreakingJob;
        }

        //private void Channel_PickTheFreakingJob(int channel)
        //{
        //  log.DebugFormat("event fired channel:{0}", channel);
        //  CheckChannel();
        //}

        void timerBackup_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {

            //lock (this.timerBackup)
            //{
            //    if (!timer.Enabled)
            //    {
            //        Console.WriteLine("[CCBackTimer] Kicks in. Checking channel");
            //        CheckChannel();
            //    }
            //}
            ////timerBackup.Enabled = true;
            //timerBackup.Start();
        }

        void timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            //timer.Stop();
            //lock (this.timer)
            //{
            //  //log.DebugFormat("[CCTimer-{0}] Kicks in!", moduleList);
            //  Console.WriteLine("[CCTimer-{0}] Kicks in!", moduleList);
            //  CheckChannel();
            //}
            //timer.Start();
        }

        #region deprecated
        private void MonitorQueue(string xAgentID)
        {
            Console.WriteLine("Monitoring Queue...{0}", moduleList);
            log.DebugFormat("Monitoring Queue...{0}", moduleList);
            string chan = moduleList.ToString().Remove(0, moduleList.ToString().LastIndexOf(",") + 1);
            int rowCount = 0;
            //SqlCommand cmdMonitorQueue = thisConnection.CreateCommand();
            //      cmdMonitorQueue.CommandText = @"
            //            SELECT [xMsgTransmitID]     
            //            FROM [dbo].[fxtb_Queue_2] with (NOLOCK)
            //            WHERE [xPickable] = 1
            //            AND [xAgentID] = @xAgentID
            //            AND [xPortNo] = @xPortNo";
            //      cmdMonitorQueue.Parameters.AddWithValue("@xAgentID", xAgentID);
            //      cmdMonitorQueue.Parameters.AddWithValue("@xPortNo", chan);
            //      cmdMonitorQueue.Notification = null;
            string cmd = string.Format(@"SELECT count ([xMsgTransmitID])     
            FROM [dbo].[fxtb_Queue_2] with (NOLOCK)
            WHERE [xPickable] = 1
            AND [xAgentID] = {0}
            AND [xPortNo] = {1}", xAgentID, chan);
            //SqlDependency dependency = new SqlDependency(cmdMonitorQueue);
            try
            {

                //thisConnection.Open();
                //using (SqlDataReader reader = cmdMonitorQueue.ExecuteReader())
                //{
                //  while (reader.Read())
                //  {
                //    rowCount++;
                //  }
                //}
                using (SqlConnection conn = new SqlConnection(connString))
                {
                    rowCount = conn.Query<Int32>(cmd).First();
                }
                sqlDependencyError = false;
            }
            catch (Exception m)
            {
                Console.WriteLine("[MonitorQueue] Error: {0}", m.Message);
                log.ErrorFormat("[MonitorQueue] Error: {0} for channel {1}", m.Message, chan);
                sqlDependencyError = true;
                //this.timer.Enabled = true;
            }
            finally
            {
                //thisConnection.Close();
            }
            Console.WriteLine("[CC{1}]Current unprocessed jobs in queue [{0}]", rowCount, moduleList);
            //log.DebugFormat("[CC{1}]Current unprocessed jobs in queue [{0}]", rowCount, moduleList);
            if (rowCount > 0)
            {
                //CheckChannel();
                Console.WriteLine("Monitor Q rowcount:{0} , proceed enable timer", rowCount);
                //this.timer.Enabled = true;
            }
            else
            {
                if (!sqlDependencyError)
                {
                    Console.WriteLine("row count 0, disable timer. start dependency.");
                    //this.timer.Enabled = false;
                    //dependency.OnChange += new OnChangeEventHandler(OnJobDetected);
                }
                else
                {
                    //this.timer.Enabled = true;
                }
            }
        }

        private void OnJobDetected(object sender, SqlNotificationEventArgs e)
        {
            SqlDependency dependency = sender as SqlDependency;
            Console.WriteLine("SqlChangedInfo: {0}", e.Info.ToString());
            //log.DebugFormat("SqlChangedInfo: {0}", e.Info.ToString());
            dependency.OnChange -= OnJobDetected;
            if (e.Info == SqlNotificationInfo.Insert || e.Info == SqlNotificationInfo.Update)
            {
                // Fire the event
                sqlDependencyError = false;
                Console.WriteLine("[CC{0}]New Job Detected!", moduleList);
                //log.DebugFormat("[CC{0}]New Job Detected!", moduleList);
                CheckChannel(false);
            }
            else
            {
                sqlDependencyError = true;
                //this.timer.Enabled = true;
            }
        }
        #endregion

        private async Task MonitorQueueChannel(string xAgentID)
        {
            Console.WriteLine("monitor queue channel...{0}", moduleList);
#if DEBUG
            log.DebugFormat("monitor queue channel {0}, Agent: {1}", moduleList, xAgentID);
#endif

            string chan = moduleList.ToString().Remove(0, moduleList.ToString().LastIndexOf(",") + 1);
            rowCount = 0;
            #region deprecated
            //      SqlCommand cmd = this.thisConnection.CreateCommand();
            //      cmd.CommandText = @"SELECT [xMsgTransmitID]     
            //            FROM [dbo].[fxtb_Queue_2] with (NOLOCK)
            //            WHERE [xPickable] = 1
            //            AND [xAgentID] = @xAgentID
            //            AND [xPortNo] = @xPortNo";
            //      cmd.Parameters.AddWithValue("@xAgentID", xAgentID);
            //      cmd.Parameters.AddWithValue("@xPortNo", chan);
            //      cmd.Notification = null;
            //string cmd = string.Format(@"SELECT count ([xMsgTransmitID])     
            //FROM [dbo].[fxtb_Queue_2] with (NOLOCK)
            //WHERE [xPickable] = 1
            //AND [xAgentID] = {0}
            //AND [xPortNo] = {1}", xAgentID, chan); 
            #endregion
            try
            {
                #region deprecated
                //using(thisConnection)
                //{
                //this.thisConnection.Open();
                //using (SqlDataReader rdr = cmd.ExecuteReader())
                //{
                //  while (rdr.Read())
                //  {
                //    rowCount++;
                //  }
                //} 
                //}
                //using (SqlConnection thisConnection = new SqlConnection(connString))
                //{
                //    rowCount = thisConnection.Query<Int32>(cmd).First();
                //    Console.WriteLine("[CC]MonitorQChannel Agent:{0} - {1} = {2}", xAgentID, chan, rowCount);
                //} 
                #endregion
                var res = await _request.Execute<GetQueueCountRequestModel, ResponseModel<Int32>>(
                  new GetQueueCountRequestModel { AgentID = xAgentID, Channel = chan },
                  Api.Get.GetChannelQueue, RequestType.GET);
                if (res.IsSuccess())
                {
                    rowCount = res.Data;
#if DEBUG
                    log.DebugFormat("Current items in queue [chan:{1}]: {0}", res.Data, chan);
#endif
                }

            }
            catch (Exception err)
            {
                Console.WriteLine("monitor queue channel error: {0}", err.Message);
                log.ErrorFormat("monitor queue channel {1} error: {0}", err.Message, moduleList);
            }
            finally
            {
                //if (this.thisConnection.State != System.Data.ConnectionState.Closed)
                //{
                //  //this.thisConnection.Close();
                //  //this.thisConnection.Dispose();
                //  //cmd.Dispose();
                //}
            }
        }
        
        public bool IsChannelBusy()
        {
            if (isEFDR)
            {
                int len = moduleList.ToString().LastIndexOf(",");
                string mod = moduleList.ToString().Substring(1, len - 1);
                string chan = moduleList.ToString().Remove(0, moduleList.ToString().LastIndexOf(",") + 1);
                return faxAgent.GetChanState(int.Parse(mod), int.Parse(chan)).ToUpper() != "IDLE";
            }
            else
            {
                int len = moduleList.ToString().LastIndexOf(",");
                string mod = moduleList.ToString().Substring(1, len - 1);
                string chan = moduleList.ToString().Remove(0, moduleList.ToString().LastIndexOf(",") + 1);
                return faxAgentFA.GetChanState(int.Parse(mod), int.Parse(chan)).ToUpper() != "IDLE";
            }
        }

        public void CheckChannel(bool wait)
        {
            //timer.Enabled = true;// false;
            //timer.Stop();
            //timerBackup.Enabled = true;// false;
            //timerBackup.Stop();

            if (wait)
            {
                System.Threading.Thread.Sleep(15000);
            }


            string guid = Guid.NewGuid().ToString();

            try
            {
                //call qjob method
                if (IsServerOnline())
                {
                    if (isEFDR)
                    {
                        int len = moduleList.ToString().LastIndexOf(",");
                        string mod = moduleList.ToString().Substring(1, len - 1);
                        string chan = moduleList.ToString().Remove(0, moduleList.ToString().LastIndexOf(",") + 1);
                        //log.DebugFormat("Lapsed event: Len={0}; Mod={1}; Chan={2}", len, mod, chan);
                        bool chanEnableStatus = faxAgent.IsChannelEnabled(int.Parse(mod), int.Parse(chan));
                        bool chanSendEnable = faxAgent.IsChannelSendEnabled(int.Parse(mod), int.Parse(chan));
                        bool chanIsActive = faxAgent.GetChanIsActive(int.Parse(mod), int.Parse(chan));
                        string chanState = faxAgent.GetChanState(int.Parse(mod), int.Parse(chan));
#if DEBUG
                        Console.WriteLine("Check channel state {0},{1}: Enabled:{2}, Send{3}, Busy:{4}", mod, chan, chanEnableStatus, chanSendEnable, chanIsActive);
                        log.DebugFormat("Check channel state {0},{1}: Enabled:{2}, Send{3}, Busy:{4}", mod, chan, chanEnableStatus, chanSendEnable, chanIsActive);
#endif
                        if (chanEnableStatus && chanSendEnable)
                        {
                            if (faxAgent.GetChanState(int.Parse(mod), int.Parse(chan)).ToUpper() == "IDLE")
                            {
                                RetrieveQJob(moduleList.ToString(), guid, chan)
                                    .GetAwaiter()
                                    .GetResult();
                            }
                            else
                            {
                                ////reset the timer to pick again
                                //timer.Interval = timerInterval;
                                //if (!_useTimer)
                                //{
                                //    timer.Interval = timerInterval + 5000;
                                //    //timer.Enabled = true;
                                //    timer.Start();
                                //}
                                Console.WriteLine("[CCDR]Timer reset to pick when channel not idle");
                            }
                        }
                    }
                    else
                    {
                        int len = moduleList.ToString().LastIndexOf(",");
                        string mod = moduleList.ToString().Substring(1, len - 1);
                        string chan = moduleList.ToString().Remove(0, moduleList.ToString().LastIndexOf(",") + 1);
                        //log.DebugFormat("[CC]Lapsed event: Len={0}; Mod={1}; Chan={2}", len, mod, chan);
                        bool chanEnableStatus = faxAgentFA.IsChannelEnabled(int.Parse(mod), int.Parse(chan));
                        bool chanSendEnable = faxAgentFA.IsChannelSendEnabled(int.Parse(mod), int.Parse(chan));
                        bool chanIsActive = faxAgentFA.GetChanIsActive(int.Parse(mod), int.Parse(chan));
                        string chanState = faxAgentFA.GetChanState(int.Parse(mod), int.Parse(chan));
#if DEBUG
                        Console.WriteLine("Check channel state {0},{1}: Enabled:{2}, Send{3}, Busy:{4}", mod, chan, chanEnableStatus, chanSendEnable, chanIsActive);
                        log.DebugFormat("Check channel state {0},{1}: Enabled:{2}, Send{3}, Busy:{4}", mod, chan, chanEnableStatus, chanSendEnable, chanIsActive);
#endif
                        if (chanEnableStatus && chanSendEnable)
                        {
                            if (faxAgentFA.GetChanState(int.Parse(mod), int.Parse(chan)).ToUpper() == "IDLE")
                            {
                                RetrieveQJob(moduleList.ToString(), guid, chan)
                                    .GetAwaiter()
                                    .GetResult();
                            }
                            else
                            {
                                //reset the time to pick again
                                //timer.Interval = timerInterval;
                                //if (_useTimer)
                                //{
                                //    timer.Interval = timerInterval + 5000;
                                //    //timer.Enabled = true;
                                //}
                                Console.WriteLine("[CC]Timer reset to pick when channel not idle");
                            }
                        }
                    }
                }
            }
            catch (Exception err)
            {
                if (isEFDR)
                {
                    log.ErrorFormat("[CCDR-{0}]check channel err: {1}", moduleList, err.Message);
                }
                else
                {
                    log.ErrorFormat("[CC-{0}]check channel err: {1}", moduleList, err.Message);
                }
            }
            finally
            {
                //timer.Enabled = true;
                Console.WriteLine("done check channel.");
                //MonitorQueue(agentID);
                //timerBackup.Enabled = true;
                //MonitorQueueChannel(agentID)
                //    .GetAwaiter()
                //    .GetResult();
                if (rowCount > 0)
                {
                    //timer.Enabled = true;
#if DEBUG
                    log.DebugFormat("restart timer to pick next job in queue. timer state:" /*{0}", timer.Enabled*/);
#endif
                    //timer.Start();
                }
                else if (_useTimer)
                {
                    //timer.Enabled = true;
                    //timer.Start();
                }
                //Console.WriteLine("Timer state: {0}", timer);
            }
        }


        #region fetch
        private async Task RetrieveQJob(string module, string guid, string port)
        {
            string _dateTag = DateTime.Today.ToString("yyyyMMdd");
            string _completePath = string.Format(@"{0}xfax\completed\{1}\", AppDomain.CurrentDomain.BaseDirectory, _dateTag);
            if (isEFDR)
            {
                _completePath = string.Format(@"{0}xfaxDR\completed\{1}\", AppDomain.CurrentDomain.BaseDirectory, _dateTag);
            }

            try
            {
                #region deprecated
                //retrieve job from q over web service
                //if (client.State != CommunicationState.Opened)
                //{
                //  if (client.State== CommunicationState.Closed)
                //  {
                //    client = new wfFaxAgentClient(this.Binding, EndPointAddr);
                //  }
                //  client.Open();
                //}
                //object[] inputs = new object[]{Convert.ToInt32(agentID),module};
                //log.Debug("wcf input count: " + inputs.Length.ToString());
                //FXC3.Entity.Job.FaxJobQ job = CallWcf(WCFMethod.GetQMessage, inputs) as FaxJobQ; 
                #endregion
                {
                    string consoleNote = string.Empty;
                    var job = await _request.Execute<GetQueueMessageRequestModel, ResponseModel<FaxJobQ>>(
                        new GetQueueMessageRequestModel
                        {
                            AgentId = Convert.ToInt32(agentID),
                            Module = module
                        }, "/api/faxagent/messageq", RequestType.GET);

                    if (job != null && job.IsSuccess() && job.Data != null)
                    {
                        if (job.Data.TransmissionID == 0)
                        {
                            consoleNote = "Interfax job detected.";
                            goto INTERNALFAX;
                        }
                        Console.WriteLine("Job retrieve from fax queue. ID:{0}", job.Data.TransmissionID.ToString());

                        //manage tif streaming.
                        Console.WriteLine("begin file streaming");
                        log.DebugFormat("[CC{1}]Job tif info: {0}", Encoding.UTF8.GetString(job.Data.TIFF), module);

                        log.DebugFormat("[RetrieveQJob]GetQueueImageRequestModel TransmissionID: {0}", job.Data.TransmissionID);
                        var response = await _request.Execute<GetQueueImageRequestModel, ResponseModel<byte[]>>(
                            new GetQueueImageRequestModel
                            {
                                ImgId = Convert.ToBase64String(job.Data.TIFF)
                            }, Api.Get.GetImageQueue, RequestType.GET);  // "/api/faxagent/imageq"
                        try
                        {
                            //string savePath =  SaveTif(_dateTag + "\\" + job.FileName, new byte[0]);
                            //File.Delete(savePath);
                            Console.WriteLine("Total binary size after download: {0}", response.Data.Length);

                            log.DebugFormat("[RetrieveQJob] SaveTif");
                            SaveTif(_dateTag + "\\" + job.Data.FileName, response.Data);
                        }
                        catch (Exception errDown)
                        {
                            log.Error("File stream download error: " + errDown.Message);
                            EventLog.WriteEntry("FaxAgent", "FaxAgent File Download Error: " + errDown.Message, EventLogEntryType.Error, 218);
                        }
                        log.DebugFormat("[RetrieveQJob]Delay before send for {0} seconds",_channelDelay);
                        Thread.Sleep(new TimeSpan(0, 0, _channelDelay));
                        #region deprecated
                        //FXC3.Entity.Job.FaxJobQ jobb = new FXC3.Entity.Job.FaxJobQ
                        //{
                        //    AttemptNo = job.AttemptNo,
                        //    CallerID = job.CallerID,
                        //    CSID = job.CSID,
                        //    CustomHeaderString = job.CustomHeaderString,
                        //    DialString = job.DialString,
                        //    FaxResolution = job.FaxResolution,
                        //    FileName = job.FileName,
                        //    IsAutoResumeOn = job.IsAutoResumeOn,
                        //    PageStartNo = job.PageStartNo,
                        //    PortNo = job.PortNo,
                        //    RecipientName = job.RecipientName,
                        //    SenderName = job.SenderName,
                        //    TIFF = job.TIFF,
                        //    TransmissionID = job.TransmissionID
                        //}; 
                        #endregion
                        log.DebugFormat("[RetrieveQJob] SendFax");
                        SendFax(job.Data, _dateTag, guid)
                            .GetAwaiter()
                            .GetResult();
                    INTERNALFAX:
                        //do nothing
                        Console.WriteLine(consoleNote);
                        //this.timerBackInterval = timerInterval;
                        //this.timerBackup.Interval = timerInterval;
                        //this.timer.Interval = timerInterval;
                        //this.timer.Enabled = true
                        _useTimer = true;
                    }
                    else
                    {
                        //this.timer.Interval = timerInterval + 1000;
                        //enableTimer = false;
                        _useTimer = false;
                        //this.timerBackInterval += 1000;
                        //this.timerBackup.Interval = timerBackInterval;
                    }
                }
            }
            catch (Exception err)
            {
                if (isEFDR)
                {
                    log.Error("[CCDR]retrieve qjob error: " + err.Message);
                    EventLog.WriteEntry("FaxAgent[CCDR]", "Retrieve Job Error: " + err.Message, EventLogEntryType.Error, 75);
                }
                else
                {
                    log.Error("[CC]retrieve qjob error: " + err.Message);
                    EventLog.WriteEntry("FaxAgent[CC]", "Retrieve Job Error: " + err.Message, EventLogEntryType.Error, 75);
                }
                //jumpstart cchannel code
                //this.EnableTimer();
            }
            finally
            {
                //if (client.State == CommunicationState.Opened)
                //{
                //  client.Close();
                //}
                //Console.WriteLine("Timer interval set to: {0}", timer.Interval.ToString());
                //log.InfoFormat("Timer interval set to: {0}",timer.Interval.ToString());
                if (_useTimer)
                {
                    //this.timer.Enabled = enableTimer; //redundant. timer should be handled at one place only. #SAM 20190417
                }
            }
        }
        #endregion

        #region send
        private async Task SendFax(FaxJobQ job, string dateFol, string guid)
        {
            log.DebugFormat("[SendFax] Start");
            string result = string.Empty;
            try
            {
                //Console.WriteLine("***Outbound fax job info:");
                //Console.WriteLine("Attempt no: {0}", job.AttemptNo.ToString());
                //Console.WriteLine("Caller ID: {0}", job.CallerID);
                //Console.WriteLine("CSID: {0}", job.CSID);
                //Console.WriteLine("Custom header string: {0}", job.CustomHeaderString);
                //Console.WriteLine("Dial String: {0}", job.DialString);
                //Console.WriteLine("Fax resolution: {0}", job.FaxResolution);
                //Console.WriteLine("Auto-resume: {0}", job.IsAutoResumeOn.ToString());
                //Console.WriteLine("Port no: {0}", job.PortNo.ToString());
                //Console.WriteLine("Recipient Name: {0}", job.RecipientName);
                //Console.WriteLine("Sender Name: {0}", job.SenderName);
                //Console.WriteLine("Tranmission ID: {0}", job.TransmissionID.ToString());

                log.InfoFormat("***Outbound fax job info:");
                log.InfoFormat("Attempt no: {0}", job.AttemptNo.ToString());
                log.InfoFormat("Caller ID: {0}", job.CallerID);
                log.InfoFormat("CSID: {0}", job.CSID);
                log.InfoFormat("Custom header string: {0}", job.CustomHeaderString);
                log.InfoFormat("Dial String: {0}", job.DialString);
                log.InfoFormat("Fax resolution: {0}", job.FaxResolution);
                log.InfoFormat("Auto-resume: {0}", job.IsAutoResumeOn.ToString());
                log.InfoFormat("Port no: {0}", job.PortNo.ToString());
                log.InfoFormat("Recipient Name: {0}", job.RecipientName);
                log.InfoFormat("Sender Name: {0}", job.SenderName);
                log.InfoFormat("Tranmission ID: {0}", job.TransmissionID.ToString());

                if (job.IsAutoResumeOn)
                {
                    int page = job.PageStartNo;   //page start no
                    if (page > 1)
                    {
                        //tbc
                        job.PageStartNo = page - 1;
                    }
                    else
                    {
                        job.PageStartNo = 0;
                    }
                }
                else
                {
                    job.PageStartNo = 0;
                }

                if (isEFDR)
                {
                    log.DebugFormat("[SendFax] isEFDR Go");
                    result = faxAgent.SendFax(job.TransmissionID.ToString(), job.DialString,
                      AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\archive\" + dateFol + "\\" + job.FileName,
                      job.PortNo, job.CSID, job.CallerID, job.FaxResolution, job.CustomHeaderString, job.RecipientName, job.PageStartNo, job.TimeZone);
                }
                else
                {
                    log.DebugFormat("[SendFax] Go");

                    int len = moduleList.ToString().LastIndexOf(",");
                    string mod = moduleList.ToString().Substring(1, len - 1);
                    string chan = moduleList.ToString().Remove(0, moduleList.ToString().LastIndexOf(",") + 1);
                    if (string.IsNullOrEmpty(job.CSID))
                    {
                        job.CSID = faxAgentFA.GetChanLocalID(int.Parse(mod), int.Parse(chan));
                        //Console.WriteLine("[CC]LocaId value: {0}", job.CSID);
                        log.DebugFormat("[SendFax] [CC]LocaId value: {0}", job.CSID);
                    }

                    log.DebugFormat("[SendFax] [CC]TransmissionID: {0}", job.TransmissionID);

                    switch (faxAgentFA.FaxAgentDriverType)
                    {
                        case FaxCore.Common.FaxDriverType.XCapi:
                            result = faxAgentFA.SendFax(job.TransmissionID.ToString(), job.DialString,
                      AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive\" + dateFol + "\\" + job.FileName,
                      job.CSID, job.CallerID, job.FaxResolution, job.CustomHeaderString, job.RecipientName, job.PageStartNo, job.TimeZone);
                            log.DebugFormat("Execute send job [{1}] with random port: {0}", result, job.DialString);
                            break;
                        default:
                            result = faxAgentFA.SendFax(job.TransmissionID.ToString(), job.DialString,
                      AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive\" + dateFol + "\\" + job.FileName,
                      job.PortNo, job.CSID, job.CallerID, job.FaxResolution, job.CustomHeaderString, job.RecipientName, job.PageStartNo, job.TimeZone);
                            break;
                    }                   
                }

                if (isEFDR)
                {
                    log.Debug("[CCDR]send fax result(remote):" + job.PortNo.ToString() + " - " + result + " | guid:" + guid);
                    //Console.WriteLine("[CCDR]send fax result(remote) - Port:{0} | GUID:{1}", job.PortNo.ToString(), guid);
                }
                else
                {
                    log.Debug("[CC]send fax result(remote):" + job.PortNo.ToString() + " - " + result + " | guid:" + guid);
                    //Console.WriteLine("[CC]send fax result(remote) - Port:{0} | GUID:{1}", job.PortNo.ToString(), guid);
                }

                //retry when channel unavailable
                if (result.Equals("ChannelUnavailable"))
                {
                    Thread.Sleep(500);
                    if (isEFDR)
                    {
                        log.DebugFormat("[SendFax] ChannelUnavailable isEFDR Go");

                        result = faxAgent.SendFax(job.TransmissionID.ToString(), job.DialString, AppDomain.CurrentDomain.BaseDirectory + @"\xfaxDR\archive\" + dateFol + "\\" + job.FileName,
                                job.PortNo, job.CSID, job.CallerID, job.FaxResolution, job.CustomHeaderString, job.RecipientName, job.PageStartNo, job.TimeZone);
                    }
                    else
                    {
                        log.DebugFormat("[SendFax] ChannelUnavailable Go");

                        switch (faxAgentFA.FaxAgentDriverType)
                        {                            
                            case FaxCore.Common.FaxDriverType.XCapi:
                                result = faxAgentFA.SendFax(job.TransmissionID.ToString(), job.DialString, AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive\" + dateFol + "\\" + job.FileName,
                                 job.CSID, job.CallerID, job.FaxResolution, job.CustomHeaderString, job.RecipientName, job.PageStartNo, job.TimeZone);
                                log.DebugFormat("Execute send job after wait [{1}] with random port: {0}", result, job.DialString);
                                break;
                            default:
                                result = faxAgentFA.SendFax(job.TransmissionID.ToString(), job.DialString, AppDomain.CurrentDomain.BaseDirectory + @"\xfax\archive\" + dateFol + "\\" + job.FileName,
                                job.PortNo, job.CSID, job.CallerID, job.FaxResolution, job.CustomHeaderString, job.RecipientName, job.PageStartNo, job.TimeZone);
                                break;
                        }

                        
                    }

                    log.Debug("[CC]send fax result after retry(remote):" + job.PortNo.ToString() + " - " + result + " | guid:" + guid);
                    //Console.WriteLine("[CC]send fax result({2}) after retry(remote): - Port:{0} | GUID:{1}", job.PortNo.ToString(), guid, result);
                }


                if (result.Equals("InvalidOrMissingFile") || result.Equals("InvalidChannel") || result.Equals("InvalidOrMissingNumber")
                  || result.Equals("NoChannelsAvailable") || result.Equals("ChannelUnavailable") || result.Equals("FileNotFound")
                  || result.Equals("FileError") || result.Equals("DestinationBlackListed") || result.Equals("CallBlocked")
                  || result.Equals("RemoteDisconnect") || result.Equals("NegotiationError") || result.Equals("TrainingError")
                  || result.Equals("Unauthorized") || result.Equals("InvalidParameter") || result.Equals("NotImplemented")
                  || result.Equals("ItemNotFound") || result.Equals("EncryptionDisabled") || result.Equals("EncryptionRequired")
                  || result.Equals("DecryptFailure") || result.Equals("DocumentRejected") || result.Equals("DocumentNotSupported")
                  || result.Equals("DocumentTooLarge"))
                //if (!result.Equals("InProgress"))
                {
                    //log.Debug("[CC]Instant return: Port-" + job.PortNo + "Port Status: " + faxAgent.GetChanState(4, job.PortNo));
                    //Console.WriteLine("[CC]Instant return: Port-{0} | Port state:{1}", job.PortNo.ToString(), faxAgent.GetChanState(4, job.PortNo));
                    FaxJobOut jobObj = new FaxJobOut();
                    jobObj.Channel = job.PortNo;
                    jobObj.ConnectSpeed = 0;
                    jobObj.ConnectTime = 0;
                    jobObj.FaxEncoding = string.Empty;
                    jobObj.OutFaxResult = result;
                    jobObj.PagesDelivered = 0;
                    jobObj.RemoteId = string.Empty;
                    jobObj.TAG = job.TransmissionID;
                    jobObj.ExtendedResult = 4000;  //temp change from 4000
                    jobObj.DateOut = DateTime.UtcNow;

                    if (result.Equals("ChannelUnavailable") || result.Equals("NoChannelsAvailable"))
                    {
                        jobObj.ExtendedResult = 13;
                    }

                    //client.Outgoing(jobObj, GetIPAddress(), agentID);
                    object[] inputs = new object[] { jobObj, GetIPAddress(), agentID };
                    await CallWcf(WCFMethod.OutGoing, inputs);
                }
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("[CC]Send Fax CChannel", err.Message, EventLogEntryType.Error, 216);
            }

            log.DebugFormat("[SendFax] End");
        }
        #endregion

        #region helper

        //return server ip address
        private string GetIPAddress()
        {
            string result = string.Empty;
            try
            {
                System.Net.IPHostEntry hostIP = (System.Net.IPHostEntry)System.Net.Dns.GetHostEntry(GetServerName());
                foreach (IPAddress ip in hostIP.AddressList)
                {
                    if (!ip.AddressFamily.Equals(AddressFamily.InterNetworkV6))
                    {
                        result = ip.ToString();
                    }
                }
                //log.Info("[CC]Server IP: " + result);
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("[CC]Get IP", err.Message, EventLogEntryType.Error, 337);
            }
            return result;
        }

        //return server name
        private string GetServerName()
        {
            string result = Environment.MachineName;
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
                EventLog.WriteEntry("FaxAgent[CC]", "Create Directory Error: " + err.Message, EventLogEntryType.Error, 74);
                result = false;
            }
            return result;
        }

        private void MoveTif(string from, string to)
        {
            try
            {
                if (CheckDirectoryExist(to))
                {
                    string filename = Path.GetFileName(from);
                    File.Move(from, to + filename);
                }
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent[CC]", "Move File Error: " + err.Message, EventLogEntryType.Error, 98);
            }
        }

        private string SaveTif(string filename, byte[] binary)
        {
            string result = string.Empty;
            FileStream fs = null;
            BinaryWriter bw = null;

            try
            {
                string fld = AppDomain.CurrentDomain.BaseDirectory + @"xfax\archive\" + filename.Substring(0, filename.LastIndexOf("\\"));
                if (isEFDR)
                {
                    fld = AppDomain.CurrentDomain.BaseDirectory + @"xfaxDR\archive\" + filename.Substring(0, filename.LastIndexOf("\\"));
                }
                result = fld;
                CheckDirectoryExist(fld);
                log.Debug("save file action: " + fld + "; filename: " + filename);
                Console.WriteLine("save file action: " + fld + "; filename: " + filename);
                log.Debug("save file size: " + binary.Length.ToString());
                Console.WriteLine("save file size: " + binary.Length.ToString());

                if (isEFDR)
                {
                    if (!File.Exists(AppDomain.CurrentDomain.BaseDirectory + @"xfaxDR\archive\" + filename))
                    {
                        fs = new FileStream(AppDomain.CurrentDomain.BaseDirectory + @"xfaxDR\archive\" + filename, FileMode.Create);
                        bw = new BinaryWriter(fs);
                        bw.Write(binary);
                        bw.Close();
                        fs.Close();
                        fs.Dispose();
                    }
                }
                else
                {
                    if (!File.Exists(AppDomain.CurrentDomain.BaseDirectory + @"xfax\archive\" + filename))
                    {
                        fs = new FileStream(AppDomain.CurrentDomain.BaseDirectory + @"xfax\archive\" + filename, FileMode.Create);
                        bw = new BinaryWriter(fs);
                        bw.Write(binary);
                        bw.Close();
                        fs.Close();
                        fs.Dispose();
                    }
                }
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent[CC]", "Save File Error: " + err.Message, EventLogEntryType.Error, 114);
            }
            return result;
        }

        private bool IsServerOnline()
        {
            bool result = false;
            string s = string.Empty;
            try
            {
                //if (client.State != CommunicationState.Opened)
                //{
                //  client.Open();  
                //}

                //s = client.ServerStatus();
                //if (!string.IsNullOrEmpty(s))
                //{
                //  result=true;
                //}
                result = this.ServerStatus;
            }
            catch (Exception err)
            {
                //log.Error("Check server online error: " + err.Message);
                result = false;
            }
            return result;
        }


        private async Task<object> CallWcf(WCFMethod method, object[] inputList)
        {
            object returnObj = null;
            string _call = string.Empty;
            //client = new wfFaxAgentClient(this.Binding, this.EndPointAddr);
            try
            {
                //client.Open();

                switch (method)
                {
                    case WCFMethod.GetQMessage:
                        _call = method.ToString();
                        returnObj = await _request.Execute<GetQueueMessageRequestModel, ResponseModel<FaxJobQ>>(
                                    new GetQueueMessageRequestModel
                                    {
                                        AgentId = (int)inputList[0],
                                        Module = (string)inputList[1]
                                    }, Api.Get.GetMessageQueue, RequestType.GET);  // "/api/faxagent/messageq"
                        break;
                    case WCFMethod.OutGoing:
                        _call = method.ToString();
                        #region deprecated
                        //var outJob = (FXC3.Entity.Job.FaxJobOut)inputList[0];
                        //FX.FXC3.Entity.Job.FaxJobOut fjo = new FX.FXC3.Entity.Job.FaxJobOut
                        //{
                        //    Channel = outJob.Channel,
                        //    ConnectSpeed = outJob.ConnectSpeed,
                        //    ConnectTime = outJob.ConnectTime,
                        //    DateOut = outJob.DateOut,
                        //    ExtendedResult = outJob.ExtendedResult,
                        //    FaxEncoding = outJob.FaxEncoding,
                        //    OutFaxResult = outJob.OutFaxResult,
                        //    PagesDelivered = outJob.PagesDelivered,
                        //    RemoteId = outJob.RemoteId,
                        //    TAG = outJob.TAG
                        //}; 
                        #endregion
                        returnObj = await _request.Execute<OutboundRequestModel, ResponseModel<bool>>(
                                        new OutboundRequestModel
                                        {
                                            OutJob = (FaxJobOut)inputList[0],
                                            ServerIp = (string)inputList[1],
                                            AgentId = (string)inputList[2]
                                        }, Api.Post.Outbound, RequestType.POST);  //"/api/faxagent/outbound"
                        break;
                }
                //client.Close();
            }
            catch (Exception err)
            {
                log.Error(string.Format("CallAPI {0} return error: {1}", _call, err.ToString()));
            }
            return returnObj;
        }

        private enum WCFMethod
        {
            GetQMessage,
            OutGoing,
        }

        #endregion

    }
}