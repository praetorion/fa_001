using FaxCore.Common;
using FaxCore.FaxServer;
using FXC6.Entity.Api;
using FXC6.Entity.Job;
using FXC6.FaxAgentHost;
using FXC6.Secure;
using log4net;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace FaxAgentSvc.Context
{
  public class FaxServerHostContext : IFaxAgent
    {
        #region Private Declarations
        private FaxServer _server;
        private readonly ILog log;
        #endregion

        #region Public Properties
        /// <summary>
        /// Number of channels to initialize
        /// </summary>
        public int IniChannels { get; set; } = 0;

        /// <summary>
        /// Error logging level. ALL/DEBUG/ERROR/FATAL  
        /// </summary>
        public Enums LogLevel { get; set; }

        /// <summary>
        /// Enable/disable barcode scanning
        /// </summary>
        public bool BarCodeScan { get; set; }

        /// <summary>
        /// max number of log file created
        /// </summary>
        public int LogFileCount { get; set; } = 5;

        /// <summary>
        /// Set max log file size in KB
        /// </summary>
        public int LogFileSize { get; set; } = 10240;

        public FaxDriverType FaxAgentDriverType { get; set; } = FaxDriverType.EtherFax;

        /// <summary>
        /// Auto convert all inbound fax image to fine resolution (204x196 dpi)
        /// </summary>
        public bool AdjustFaxResolution { get; set; }
        #endregion

        #region Constructor
        public FaxServerHostContext()
        {
            log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        }
        #endregion

        #region Operations

        #region Events
        public void InitializeAgent()
        {
      Console.WriteLine("Initilizing agent: {0}", FaxAgentDriverType);
            _server = new FaxServer(FaxAgentDriverType)
            {
                LogLevel = LogLevel.ToString(),
                LogFile = $@"{AppDomain.CurrentDomain.BaseDirectory}\log\faxcore.log",
                LogFileMaxSize = LogFileSize,
                LogFileMaxBackups = LogFileCount,
                ServicePath = $@"{AppDomain.CurrentDomain.BaseDirectory}FaxTemp\"
            };
            _server.FaxReceiveEvent += server_FaxReceiveEvent;
            _server.FaxSendEvent += server_FaxSendEvent;
            _server.ChannelStatusEvent += server_ChannelStatusEvent;
        }

        private void server_ChannelStatusEvent(ChannelStatusEventArgs e)
        {
            log.DebugFormat("ChannelEvent: Channel[{0}] - Speed[{1}] - Time[{2}] - Page[{3}] - Encoding[{4}] - RemoteID[{5}]", e.Channel, e.ConnectSpeed, e.ConnectTime, e.CurrentPage, e.Encoding, e.RemoteId);
        }

        private void server_FaxSendEvent(FaxSendEventArgs e)
        {
            var dropFolder = $@"{AppDomain.CurrentDomain.BaseDirectory}\xfax\out\";
            var guid = Guid.NewGuid();

            try
            {
                log.Debug("FA start fax job(sent)");
                string filan = string.Empty;
                try
                {
                    if (!string.IsNullOrEmpty(e.RemoteId))
                    {
                        ASCIIEncoding ascii = new ASCIIEncoding();
                        byte[] byteArr = Encoding.UTF8.GetBytes(e.RemoteId);
                        byte[] asciiArr = Encoding.Convert(Encoding.UTF8, Encoding.ASCII, byteArr);
                        filan = ascii.GetString(asciiArr);
                        log.Debug("encoded OUT remote ID: " + filan);
                    }
                }
                catch (Exception errEnc)
                {
                    log.Error("ASCII encoding error", errEnc);
                    filan = string.Empty;
                }

                //process CSID
                var csid = FilterCSID(filan);
                log.Debug("csid filtered result: " + csid);

                var outJob = new FaxJobOut
                {
                    Channel = e.Channel,
                    ConnectSpeed = e.ConnectSpeed,
                    ConnectTime = e.ConnectTime,
                    DateOut=DateTime.UtcNow,
                    /*UTC comment line below, uncomment above*/
                    //DateOut = DateTime.Now,
                    ExtendedResult = e.ExtendedResult,
                    FaxEncoding = e.Encoding.ToString(),
                    OutFaxResult = e.FaxResult.ToString(),
                    PagesDelivered = e.PagesDelivered,
                    RemoteId = csid,  //e.RemoteId,
                    TAG = e.Tag
                };
                if (FaxAgentDriverType == FaxDriverType.FaxBack)
                {
                    outJob.ExtendedResult = e.ExtendedResult * 10;
                }
                log.Debug("pass job obj cr. sr for channel: " + e.Channel.ToString());

                var se = new XmlSerializer(typeof(FaxJobOut));
                using (var sw = new StreamWriter(dropFolder + guid.ToString() + ".xml"))
                {
                    se.Serialize(sw, outJob);
                }
                log.DebugFormat("SR complete for job {0} | port: {1}", e.Tag.ToString(), e.Channel.ToString());
            }
            catch (Exception err)
            {
                log.Error("Fax Send Event Error[106]", err);
                EventLog.WriteEntry("FaxAgent", err.Message, EventLogEntryType.Error, 106);
            }
        }

        private bool server_FaxReceiveEvent(FaxReceiveEventArgs e)
        {
            var result = false;
            var dropFolder = $@"{ AppDomain.CurrentDomain.BaseDirectory}\xfax\in\";
            var guid = Guid.NewGuid();
            try
            {
                string filan = string.Empty;
                try
                {
                    if (!string.IsNullOrEmpty(e.RemoteId))
                    {
                        var ascii = new ASCIIEncoding();
                        byte[] byteArr = Encoding.UTF8.GetBytes(e.RemoteId);
                        byte[] byteAscii = Encoding.Convert(Encoding.UTF8, Encoding.ASCII, byteArr);
                        filan = ascii.GetString(byteAscii);
                        log.Debug("encoded IN remote ID: " + filan);
                    }
                }
                catch (Exception err)
                {
                    log.Error("ASCII Encoding error for remote ID at receive: " + err.Message);
                }

                string csid = FilterCSID(filan);
                log.Debug("csid filter result: " + csid);

                var jobIn = new FaxJobIn
                {
                    BarcodeCount = e.ScanResults,
                    BarcodeResultFile = e.ScanResultsFile,
                    //jobIn.BarcodeXML
                    CalledNumber = e.CalledNumber,
                    CallingNumber = e.CallingNumber,
                    Channel = e.Channel,
                    ConnectSpeed = e.ConnectSpeed,
                    ConnectTime = e.ConnectTime,
                    DateIn = DateTime.UtcNow,
                    /*UTC uncomment above, comment below*/
                    //DateIn = DateTime.Now,
                    FaxEncoding = e.Encoding.ToString(),
                    InFaxResult = e.FaxResult.ToString(),
                    PagesReceived = e.PagesReceived,
                    ReceiveFile = e.ReceiveFile,
                    ReferredID = e.ReferredId,
                    RemoteId = csid,  //e.RemoteId,
                    RoutingInfo = e.RoutingInfo
                };

                var se = new XmlSerializer(typeof(FaxJobIn));
                using (var sw = new StreamWriter(dropFolder + guid + ".xml"))
                {
                    se.Serialize(sw, jobIn);
                }

                //check file exist. affect etherfax driver/job which page count 0
                if (File.Exists(e.ReceiveFile))
                {
                    result = true;
                }
                log.Debug("Fax receive event, TIF download complete: " + result.ToString());
            }
            catch (Exception err)
            {
                log.Error("Fax Receive Event Error[162]: " + err.Message);
                EventLog.WriteEntry("FaxAgent", err.Message, EventLogEntryType.Error, 162);
            }
            return result;
        }

        private static string FilterCSID(string csid)
        {
            if (csid.Length > 0)
            {
                var outStr = SecurityElement.Escape(csid.Trim());
                var re = @"[^\x09\x0A\x0D\x20-\xD7FF\xE000-\xFFFD\x10000-x10FFFF]";

                return Regex.Replace(outStr, re, "");
            }
            else
            {
                return string.Empty;
            }
        }
        #endregion

        #region IFaxAgent Interface
        public void ConfigureAllChannels()
        {
            try
            {
                for (int i = 0; i < IniChannels; i++)
                {
                    ConfigureEnableChan(i, true);
                    ConfigureHeaderStyle(i, "Default", string.Empty);
                    ConfigureReceiveEnabled(i, true);
                    ConfigureSendEnabled(i, true);
                    ConfigureToneDetectEnabled(i, false);
                    ConfigureAnswerRings(i, 3);
                    ConfigureDialTimeout(i, 60);
                    ConfigureLocalID(i, "FAXCORE");

                    FaxServerCommit();
                }
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "CfgAllChan: " + err.Message, EventLogEntryType.Error, 244);
            }
        }

        public void ConfigureAnswerRings(int module, int channel, int value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].AnswerRings = value;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public void ConfigureDialTimeout(int module, int channel, int value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].DialTimeout = value;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public void ConfigureEnableChan(int module, int channel, bool value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].Enabled = value;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public void ConfigureHeaderStyle(int module, int channel, string value, string customHeader)
        {
            HeaderStyles style;

            switch (value.ToUpper())
            {
                case "DEFAULT":
                    style = HeaderStyles.Default;
                    break;
                case "EXTENDED":
                    style = HeaderStyles.Extended;
                    break;
                case "CUSTOM":
                    style = HeaderStyles.Custom;
                    break;
                default:
                    style = HeaderStyles.None;
                    break;
            }

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].HeaderStyle = style;
                    if (style == HeaderStyles.Custom)
                    {
                        mod.FaxChannels[channel].HeaderString = customHeader;
                    }
                    break;
                }
            }
            FaxServerCommit();
        }

        public void ConfigureLocalID(int module, int channel, string value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].LocalId = value;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public void ConfigureReceiveEnabled(int module, int channel, bool value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].ReceiveEnabled = value;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public void ConfigureRemoteID(int module, int channel, string value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].RemoteId = value;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public void ConfigureSendEnabled(int module, int channel, bool value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].SendEnabled = value;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public void ConfigureToneDetectEnabled(int module, int channel, bool value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].ToneDetectEnabled = value;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public void ConfigureToneDetectDigits(int module, int channel, int value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].ToneDetectDigits = value;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public void ConfigureToneWaitTimeout(int module, int channel, int value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].ToneWaitTimeout = value;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public void ConfigureAnswerRings(int channel, int value)
        {
            _server.FaxChannels[channel].AnswerRings = value;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureDialTimeout(int channel, int value)
        {
            _server.FaxChannels[channel].DialTimeout = value;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureEnableChan(int channel, bool value)
        {
            _server.FaxChannels[channel].Enabled = value;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureHeaderStyle(int channel, string value, string customHeader)
        {
            switch (value.ToUpper())
            {
                case "DEFAULT":
                    _server.FaxChannels[channel].HeaderStyle = HeaderStyles.Default;
                    break;
                case "EXTENDED":
                    _server.FaxChannels[channel].HeaderStyle = HeaderStyles.Extended;
                    break;
                case "CUSTOM":
                    _server.FaxChannels[channel].HeaderStyle = HeaderStyles.Custom;
                    _server.FaxChannels[channel].HeaderString = customHeader;
                    break;
                default:
                    _server.FaxChannels[channel].HeaderStyle = HeaderStyles.None;
                    break;
            }
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureLocalID(int channel, string value)
        {
            _server.FaxChannels[channel].LocalId = value;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureReceiveEnabled(int channel, bool value)
        {
            _server.FaxChannels[channel].ReceiveEnabled = value;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureRemoteID(int channel, string value)
        {
            _server.FaxChannels[channel].RemoteId = value;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureSendEnabled(int channel, bool value)
        {
            _server.FaxChannels[channel].SendEnabled = value;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureToneDetectEnabled(int channel, bool value)
        {
            _server.FaxChannels[channel].ToneDetectEnabled = value;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureToneDetectDigits(int channel, int value)
        {
            _server.FaxChannels[channel].ToneDetectDigits = value;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureToneWaitTimeout(int channel, int value)
        {
            _server.FaxChannels[channel].ToneWaitTimeout = value;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureDTMFTerminator(int channel, float value)
        {
            _server.FaxChannels[channel].ToneDetectTerminator = Convert.ToInt32(value);
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureDTMFTerminator(int module, int channel, float value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].ToneDetectTerminator = Convert.ToInt32(value);
                    FaxServerCommit();
                    break;
                }
            }

        }

        public void ConfigureDTMFToneType(int channel, string value)
        {
            switch (value.ToUpper())
            {
                case "TONE":
                    _server.FaxChannels[channel].GreetingType = GreetingTypes.Tone;
                    break;
                case "WAVFILE":
                    _server.FaxChannels[channel].GreetingType = GreetingTypes.WavFile;
                    break;
                default:
                    _server.FaxChannels[channel].GreetingType = GreetingTypes.None;
                    break;
            }
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureDTMFToneType(int module, int channel, string value)
        {
            GreetingTypes greeting;

            switch (value.ToUpper())
            {
                case "TONE":
                    greeting = GreetingTypes.Tone;
                    break;
                case "WAVFILE":
                    greeting = GreetingTypes.WavFile;
                    break;
                default:
                    greeting = GreetingTypes.None;
                    break;
            }

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].GreetingType = greeting;
                    break;
                }
            }
            FaxServerCommit();
        }

        public void ConfigureDTMFToneWav(int channel, string value)
        {
            _server.FaxChannels[channel].GreetingWavFile = value;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureDTMFToneWav(int module, int channel, string value)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].GreetingWavFile = value;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public string GetDTMFToneType(int module, int channel)
        {
            string result = string.Empty;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].GreetingType.ToString();
                    break;
                }
            }
            return result;
        }

        public string GetDTMFToneWav(int module, int channel)
        {
            string result = string.Empty;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].GreetingWavFile;
                    break;
                }
            }
            return result;
        }

        public void ConfigureRecvFormat(int channel, FaxCore.Common.FaxReceiveFormats format)
        {
            _server.FaxChannels[channel].FaxReceiveFormat = format;
            _server.FaxChannels[channel].Commit();
        }

        public void ConfigureRecvFormat(int module, int channel, FaxCore.Common.FaxReceiveFormats format)
        {
            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    mod.FaxChannels[channel].FaxReceiveFormat = format;
                    FaxServerCommit();
                    break;
                }
            }
        }

        public string GetRecvFormat(int module, int channel)
        {
            string result = string.Empty;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].FaxReceiveFormat.ToString();
                }
            }
            return result;
        }

        public bool GetChanIsActive(int module, int channel)
        {
            bool result = false;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].IsActive;
                    break;
                }
            }
            return result;
        }

        public bool GetFSIsStarted()
        {
            bool result = _server.IsStarted;
            return result;
        }

        public bool IsChannelEnabled(int module, int channel)
        {
            bool result = false;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].Enabled;
                    break;
                }
            }
            return result;
        }

        public bool IsChannelSendEnabled(int module, int channel)
        {
            bool result = false;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].SendEnabled;
                    break;
                }
            }
            return result;
        }

        public bool IsChannelReceiveEnabled(int module, int channel)
        {
            bool result = false;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].ReceiveEnabled;
                    break;
                }
            }
            return result;
        }

        public string GetFSServicePath()
        {
            return _server.ServicePath;
        }
        
        public void ConfigureServicePath(string value)
        {
            _server.ServicePath = AppDomain.CurrentDomain.BaseDirectory + @"\FaxTemp\";
        }

        public string GetChanHeaderType(int module, int channel)
        {
            string result = string.Empty;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].HeaderStyle.ToString();
                    break;
                }
            }
            return result;
        }

        public string GetChanLocalID(int module, int channel)
        {
            string result = string.Empty;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].LocalId;
                    break;
                }
            }
            return result;
        }

        public string GetChanRemoteID(int module, int channel)
        {
            string result = string.Empty;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].RemoteId;
                    if (string.IsNullOrEmpty(result))
                    {
                        result = string.Empty;
                    }
                    break;
                }
            }
            return result;
        }

        public string GetFSConfigurationPath()
        {
            return _server.ServicePath;
        }

        public string GetFSDriverType()
        {
            return _server.FaxDriverType.ToString();
        }

        public string GetChanState(int module, int channel)
        {
            string result = string.Empty;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].State.ToString();
                    break;
                }
            }
            return result;
        }

        public int GetFSActiveChannel()
        {
            return _server.ActiveChannels;
        }

        public int GetFSUtilization()
        {
            return _server.Utilization;
        }

        public int GetChanToneDetectDigits(int module, int channel)
        {
            int result = 0;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].ToneDetectDigits;
                    break;
                }
            }
            return result;
        }

        public bool GetChanToneDetectEnabled(int module, int channel)
        {
            bool result = false;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].ToneDetectEnabled;
                    break;
                }
            }
            return result;
        }

        public int GetChanConnectTime(int module, int channel)
        {
            int result = 0;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].ConnectTime;
                    break;
                }
            }
            return result;
        }

        public int GetChanCurrentPage(int module, int channel)
        {
            int result = 0;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].CurrentPage;
                    break;
                }
            }
            return result;
        }

        public int GetChanConnectSpeed(int module, int channel)
        {
            int result = 0;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].ConnectSpeed;
                    break;
                }
            }
            return result;
        }

        public int GetChanAnswerRing(int module, int channel)
        {
            int result = 0;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].AnswerRings;
                    break;
                }
            }
            return result;
        }

        public int GetChanDialTimeout(int module, int channel)
        {
            int result = 0;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].DialTimeout;
                    break;
                }
            }
            return result;
        }

        public int GetChannelToneWaitTimeout(int module, int channel)
        {
            int result = 0;

            foreach (FaxModule mod in _server.FaxModules)
            {
                if (mod.Module.Equals(module))
                {
                    result = mod.FaxChannels[channel].ToneWaitTimeout;
                    break;
                }
            }
            return result;
        }

        public int GetChannelCount(int module)
        {
            int result = 0;
            try
            {
                foreach (FaxModule mod in _server.FaxModules)
                {
                    if (mod.Module == module)
                    {
                        result = mod.Channels;
                        break;
                    }
                }
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", err.Message, EventLogEntryType.Error, 935);
            }
            return result;
        }

        public ArrayList GetChannelCount()
        {
            ArrayList lst = new ArrayList();
            foreach (FaxChannel chan in _server.FaxChannels)
            {
                lst.Add(chan.Channel);
            }
            return lst;
        }

        public ArrayList GetModuleCount()
        {
            ArrayList lst = new ArrayList();

            try
            {
                int i = 0;
                foreach (FaxModule module in _server.FaxModules)
                {
                    i = module.Module;
                    lst.Add("M" + i);
                }
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "Get module list error: " + err.Message, EventLogEntryType.Error, 965);
            }
            return lst;
        }

        public string GetFSIPAddress()
        {
            string result = string.Empty;
            try
            {
                var hostIP = Dns.GetHostEntry(Dns.GetHostName());

                foreach (var ip in hostIP.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        result = ip.ToString();
                        break;
                    }
                }
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "Get IP Error: " + err.Message, EventLogEntryType.Error, 985);
            }
            return result;
        }

        public string GetFSIPv6Address()
        {
            string result = string.Empty;
            try
            {
                IPHostEntry hostIP = Dns.GetHostEntry(Dns.GetHostName());

                foreach (IPAddress ip in hostIP.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        result = ip.ToString();
                        break;
                    }
                }
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "Get IP Error: " + err.Message, EventLogEntryType.Error, 1007);
            }
            return result;
        }

        public string GetFSServerName()
        {
            return Environment.MachineName;
        }

        public string GetFSPLKCode()
        {
            throw new NotImplementedException();
        }

        public string CancelFax(int channel)
        {
            string result = string.Empty;
            try
            {
                FaxResult res = _server.CancelFax(channel);
                FaxServerCommit();
                result = res.ToString();
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "Cancel msg err: " + err.Message, EventLogEntryType.Error, 1040);
            }
            return result;
        }

        public void GetFaxJob(string file)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Basic send fax method
        /// </summary>
        /// <param name="ID">process UID</param>
        /// <param name="faxNumber">recipient fax number</param>
        /// <param name="fileID">TIF File path</param>
        /// <param name="faxChannel">channel/port number to use</param>
        /// <param name="localId">Local ID</param>
        /// <param name="resolution">Fax Resolution (Standard/Fine/SuperFine)</param>
        /// <returns>result</returns>
        public string SendFax(string ID, string faxNumber, string fileID, int faxChannel, string localId, object resolution)
        {
            log.DebugFormat("[SendFax] Start ID: {0}; DialNumber: {1}; faxChannel; {2}; localId: {3}", ID, faxNumber, faxChannel, localId);
            string result = string.Empty;
            FaxResolution reso = FaxResolution.Fine;

            switch (resolution.ToString().ToUpper())
            {
                case "STANDARD":
                    reso = FaxResolution.Standard;
                    break;
                case "FINE":
                    reso = FaxResolution.Fine;
                    break;
                case "SUPERFINE":
                    reso = FaxResolution.SuperFine;
                    break;
                case "204X98":
                    reso = FaxResolution.Standard;
                    break;
                case "204X196":
                    reso = FaxResolution.Fine;
                    break;
                default:
                    reso = FaxResolution.Standard;
                    break;
            }

            try
            {
                FaxCore.Common.FaxJob job = new FaxCore.Common.FaxJob
                {
                    DialNumber = faxNumber,
                    SendFile = fileID,
                    LocalId = localId,
                    Tag = ID,
                    FaxResolution = (FaxResolution)reso   //alternate (FaxResolution)Enum.Parse(typeof(FaxResolution), resolution.ToString())
                };

                log.DebugFormat("[SendFax] FaxResolution: {0}", job.FaxResolution);

                FaxResult res = _server.SendFax(ref job, faxChannel, false);
                result = res.ToString();
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "Send Err: " + err.Message, EventLogEntryType.Error, 1061);
            }

            log.DebugFormat("[SendFax] End ID: {0}; DialNumber: {1}; faxChannel; {2}; localId: {3}", ID, faxNumber, faxChannel, localId);
            return result;
        }

        /// <summary>
        /// Send fax with specific port number and local caller ID
        /// </summary>
        /// <param name="ID">process UID</param>
        /// <param name="faxNumber">recipient fax number</param>
        /// <param name="fileID">TIF file path</param>
        /// <param name="faxChannel">channel/port number to use</param>
        /// <param name="localId">local ID</param>
        /// <param name="localCallerId">local caller id</param>
        /// <param name="resolution">fax resolution enum</param>
        /// <returns>result</returns>
        public string SendFax(string ID, string faxNumber, string fileID, int faxChannel, string localId, string localCallerId,
          object resolution)
        {
            log.DebugFormat("[SendFax] Start ID: {0}; DialNumber: {1}; faxChannel; {2}", ID, faxNumber, faxChannel);
            string result = string.Empty;

            try
            {
                FaxCore.Common.FaxJob job = new FaxCore.Common.FaxJob
                {
                    Tag = ID,
                    DialNumber = faxNumber,
                    SendFile = fileID,
                    LocalId = localId,
                    LocalCallerId = localCallerId,
                    //FaxResolution = (FaxResolution)resolution
                };

                if (resolution.ToString().ToUpper().Equals("STANDARD") || resolution.ToString().ToLower().Equals("204x98"))
                {
                    job.FaxResolution = FaxResolution.Standard;
                }
                else if (resolution.ToString().ToUpper().Equals("FINE") || resolution.ToString().ToLower().Equals("204x196"))
                {
                    job.FaxResolution = FaxResolution.Fine;
                }
                else if (resolution.ToString().ToUpper().Equals("SUPERFINE"))
                {
                    job.FaxResolution = FaxResolution.SuperFine;
                }

                log.DebugFormat("[SendFax] FaxResolution: {0}", job.FaxResolution);

                FaxResult res = _server.SendFax(ref job, faxChannel, false);
                result = res.ToString();
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "Send Err: " + err.Message, EventLogEntryType.Error, 1100);
            }

            log.DebugFormat("[SendFax] End ID: {0}; DialNumber: {1}; faxChannel; {2}", ID, faxNumber, faxChannel);
            return result;
        }

        /// <summary>
        /// Send fax with custom fax header string and support smart resume
        /// </summary>
        /// <param name="ID">process UID</param>
        /// <param name="faxNumber">recipient fax number</param>
        /// <param name="fileID">TIF file path</param>
        /// <param name="faxChannel">channel/port number to use</param>
        /// <param name="localId">Local ID</param>
        /// <param name="localCallerId">Local Caller ID</param>
        /// <param name="resolution">Fax Resolution (Standard/Fine/Superfine)</param>
        /// <param name="headerString">custom header string</param>
        /// <param name="recipientName">Recipient name for custom header field</param>
        /// <param name="startPage">Smart Resume from this page</param>
        /// <param name="timeZone">Timezone value for outbound fax. etherfax only</param>
        /// <returns>result</returns>
        public string SendFax(string ID, string faxNumber, string fileID, int faxChannel, string localId, string localCallerId,
          object resolution, string headerString, string recipientName, int startPage, int timeZone)
        {
            log.DebugFormat("[SendFax] Start ID: {0}; DialNumber: {1}; startPage; {2}; timeZone: {3}", ID, faxNumber, startPage, timeZone);
            string result = string.Empty;

            try
            {
                FaxCore.Common.FaxJob job = new FaxCore.Common.FaxJob
                {
                    Tag = ID,
                    DialNumber = faxNumber,
                    SendFile = fileID,
                    LocalId = localId,
                    LocalCallerId = localCallerId,
                    //FaxResolution = (FaxResolution)resolution,
                    HeaderString = headerString,
                    RecipientName = recipientName,
                    StartPage = startPage,
                     TimeZone= timeZone
                };

                if (resolution.ToString().ToUpper().Equals("STANDARD") || resolution.ToString().ToLower().Equals("204x98"))
                {
                    job.FaxResolution = FaxResolution.Standard;
                }
                else if (resolution.ToString().ToUpper().Equals("FINE") || resolution.ToString().ToLower().Equals("204x196"))
                {
                    job.FaxResolution = FaxResolution.Fine;
                }
                else if (resolution.ToString().ToUpper().Equals("SUPERFINE"))
                {
                    job.FaxResolution = FaxResolution.SuperFine;
                }

                log.DebugFormat("[SendFax] FaxResolution: {0}", job.FaxResolution);

                FaxResult res = _server.SendFax(ref job, faxChannel, false);
                result = res.ToString();
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent ", "Send Err: " + err.Message, EventLogEntryType.Error, 1137);
            }

            log.DebugFormat("[SendFax] END ID: {0}; DialNumber: {1}; startPage; {2}; timeZone: {3}", ID, faxNumber, startPage, timeZone);
            return result;
        }

        /// <summary>
        /// Send fax with specific port number, local caller id and sender/recipient name for custom header support
        /// </summary>
        /// <param name="ID">process UID</param>
        /// <param name="faxNumber">recipient fax number</param>
        /// <param name="fileID">TIF file path</param>
        /// <param name="faxChannel">channel/port number to use</param>
        /// <param name="localId">Local ID</param>
        /// <param name="localCallerId">Local Caller ID</param>
        /// <param name="resolution">Fax Resolution(Standard/Fine/Superfine)</param>
        /// <param name="headerString">custom header field</param>
        /// <param name="recipientName">Recipient name for custom header field</param>
        /// <returns>result</returns>
        public string SendFax(string ID, string faxNumber, string fileID, int faxChannel, string localId, string localCallerId,
            object resolution, string headerString, string recipientName)
        {
            log.DebugFormat("[SendFax] Start ID: {0}; DialNumber: {1}; recipientName; {2}", ID, faxNumber, recipientName);

            string result = string.Empty;

            try
            {
                FaxCore.Common.FaxJob job = new FaxCore.Common.FaxJob
                {
                    Tag = ID,
                    DialNumber = faxNumber,
                    SendFile = fileID,
                    LocalId = localId,
                    LocalCallerId = localCallerId,
                    //FaxResolution = (FaxResolution)resolution,
                    HeaderString = headerString,
                    RecipientName = recipientName,
                };

                if (resolution.ToString().ToUpper().Equals("STANDARD") || resolution.ToString().ToLower().Equals("204x98"))
                {
                    job.FaxResolution = FaxResolution.Standard;
                }
                else if (resolution.ToString().ToUpper().Equals("FINE") || resolution.ToString().ToLower().Equals("204x196"))
                {
                    job.FaxResolution = FaxResolution.Fine;
                }
                else if (resolution.ToString().ToUpper().Equals("SUPERFINE"))
                {
                    job.FaxResolution = FaxResolution.SuperFine;
                }

                log.DebugFormat("[SendFax] FaxResolution: {0}", job.FaxResolution);

                FaxResult res = _server.SendFax(ref job, faxChannel, false);
                result = res.ToString();
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "Send Err: " + err.Message, EventLogEntryType.Error, 1167);
            }

            log.DebugFormat("[SendFax] End ID: {0}; DialNumber: {1}; recipientName; {2}", ID, faxNumber, recipientName);
            return result;
        }

        /// <summary>
        /// Send fax without specifying port number
        /// </summary>
        /// <param name="ID">process ID</param>
        /// <param name="faxNumber">fax number</param>
        /// <param name="fileID">TIF file</param>
        /// <param name="localID">local ID</param>
        /// <param name="resolution">Fax Resolution (Standard/Fine/SuperFine)</param>
        /// <returns>result</returns>
        public string SendFax(string ID, string faxNumber, string fileID, string localID, string localCallerId, object resolution, 
            string headerString, string receipientName, int startPage, int timeZone)
        {
            log.DebugFormat("[SendFax] Start ID: {0}; DialNumber: {1}; localId; {2}", ID, faxNumber, localID);

            string result = string.Empty;

            try
            {
                FaxCore.Common.FaxJob job = new FaxCore.Common.FaxJob
                {
                    Tag = ID,
                    DialNumber = faxNumber,
                    SendFile = fileID,
                    LocalId = localID,
                    LocalCallerId = localCallerId,
                    HeaderString = headerString,
                    RecipientName = receipientName,
                    StartPage = startPage,
                    TimeZone = timeZone
                };

                if (resolution.ToString().ToUpper().Equals("STANDARD") || resolution.ToString().ToLower().Equals("204x98"))
                {
                    job.FaxResolution = FaxResolution.Standard;
                }
                else if (resolution.ToString().ToUpper().Equals("FINE") || resolution.ToString().ToLower().Equals("204x196"))
                {
                    job.FaxResolution = FaxResolution.Fine;
                }
                else if (resolution.ToString().ToUpper().Equals("SUPERFINE"))
                {
                    job.FaxResolution = FaxResolution.SuperFine;
                }

                log.DebugFormat("[SendFax] FaxResolution: {0}", job.FaxResolution);

                FaxResult res = _server.SendFax(ref job, false);
                result = res.ToString();
            }
            catch (Exception err)
            {
                EventLog.WriteEntry("FaxAgent", "Send Err: " + err.Message, EventLogEntryType.Error, 1196);
            }

            log.DebugFormat("[SendFax] End ID: {0}; DialNumber: {1}; localId; {2}", ID, faxNumber, localID);
            return result;
        }

        public void FaxServerCommit()
        {
            _server.Commit();
        }

        public void FaxServerUpdate()
        {
            _server.Update();
        }

        public void StopFaxServer()
        {
            _server.Stop();
        }

        public void FaxServerSuspend()
        {
            _server.Suspend();
        }
        public void FaxServerResume()
        {
            _server.Resume();
        }

        public void StartFaxServer()
        {
            _server.AutoScan = BarCodeScan;
            _server.Start();
        }

        public void StartFaxServer(string address, string account, string user, string password)
        {
            try
            {
                string pass = InfoSecure.FXCDecrypt(password, "9CEBB96E053A428"); //descryption code here
                log.Info("Start EtherFAX");
                _server.AutoScan = BarCodeScan;
                var startState = _server.Start(address, account, user, pass);
                if (startState)
                {
                    //_server.Suspend();
                }

            }
            catch (Exception err)
            {
                log.Error("Unable to start etherfax DR: " + err.Message);

            }
        }

        public string SendFaxDebug(string fax, string tiff, int channel, object resolution)
        {
            throw new NotImplementedException();
        }

        public string SendFaxDebug(string fax, string tiff, int channel, object resolution, bool wait)
        {
            throw new NotImplementedException();
        }

        public string SendFaxDebug(string fax, string tiff, int channel, object resolution, bool wait, string callerID, string CSI)
        {
            throw new NotImplementedException();
        }

        public string SendFaxDebug(string fax, string tiff, int channel, object resolution, bool wait, string callerID, string CSI, string senderName, string recipientName)
        {
            throw new NotImplementedException();
        }
        #endregion

        #endregion
        public IEnumerable<ChannelStatusResponse> RetrieveModuleInfo()
        {
            var list = GetModuleCount();
            if (list != null && list.Count > 0)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    string _module = list[i].ToString();
                    int _mod = Convert.ToInt32(_module.Remove(0, 1));
                    for (int j = 0; j < GetChannelCount(_mod); j++)
                    {
                        yield return new ChannelStatusResponse
                        {
                            AgentId = Convert.ToInt32(ConfigurationManager.AppSettings["AgentID"].ToString()),
                            Module = _module,
                            Port = j,
                            ConnectTime = GetChanConnectTime(_mod, j),
                            ConnectSpeed = GetChanConnectSpeed(_mod, j),
                            CurrentPage = GetChanCurrentPage(_mod, j),
                            State = GetChanState(_mod, j),
                            IsActive = GetChanIsActive(_mod, j),
                            FaActiveChans = GetFSActiveChannel(),
                            FaUtilization = GetFSUtilization(),
                            FaIsStarted = GetFSIsStarted(),
                            RemoteId = GetChanRemoteID(_mod, j)
                        };
                    }
                }
            }
        }
        
    }
}
