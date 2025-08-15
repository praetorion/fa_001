using log4net;
using System;
using System.Collections.Specialized;
using System.Configuration;
using System.Reflection;

namespace FaxAgentSvc
{
  public class AgentConfig
  {
    private readonly ILog log;

    public string Guid
    {
      get { return "266CB95F-A65B-479b-98CD-1A8AE3E6E669"; }
    }
    public string AgentId { get; set; }
    public string Server { get; private set; }
    public string Port { get; private set; }
    public string LogLevel { get; private set; }
    public string DriverType { get; private set; }
    public int IniChannel { get; set; }
    public string CountryCode { get; private set; }
    public string AreaCode { get; private set; }
    public bool BarcodeOn { get; set; }
    public char DtmfTerminatorDigit { get; private set; }
    public int BufferSize { get; private set; }
    public string RecvFormat { get; private set; }
    public string LogFileSize { get; private set; }
    public string LogFileCount { get; private set; }
    public string EtherFaxAddr { get; private set; }
    public string EtherFaxAcc { get; private set; }
    public string EtherFaxUsr { get; private set; }
    public string EtherFaxPwd { get; private set; }
    public string EtherFaxDRAgentID { get; set; }
    public string EtherFaxDRAddr { get; private set; }
    public string EtherFaxDRAcc { get; private set; }
    public string EtherFaxDRUsr { get; private set; }
    public string EtherFaxDRPwd { get; private set; }
    public string EtherFaxDRCountryCode { get; private set; }
    public string EtherFaxDRAreaCode { get; private set; }
    public string EtherFaxDRRecvFormat { get; private set; }
    public bool EtherFaxDRSmartResume { get; private set; }
    public bool EtherFaxDRAutoAdjReso { get; private set; }
    public bool SmartResume { get; private set; }
    public bool AutoAdjReso { get; private set; }
    public int Interval { get; private set; }
    public int FaxTimeout { get; private set; }
    public int ChannelDelay { get; private set; }
    public bool IsRemoteAgent { get; private set; }
    public int UdpListenPort { get; private set; }
    public string Plk { get; set; }
    public string HostUrl { get; set; }

    public string ServiceKey { get; set; }

    public AgentConfig()
    {
      log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
      AgentId = ConfigurationManager.AppSettings["AgentID"].ToString();
      Server = ConfigurationManager.AppSettings["Server"].ToString();
      Port = ConfigurationManager.AppSettings["Port"].ToString();
      LogLevel = ConfigurationManager.AppSettings["LogLevel"].ToString();
      DriverType = ConfigurationManager.AppSettings["DriverType"].ToString();
#if DEBUG
      IniChannel = Convert.ToInt32(ConfigurationManager.AppSettings["ChannelsIni"].ToString());  //disabled for licensing
#endif
      CountryCode = ConfigurationManager.AppSettings["CountryCode"].ToString();
      AreaCode = ConfigurationManager.AppSettings["AreaCode"].ToString();
      BarcodeOn = false; //Convert.ToBoolean(Convert.ToInt32(ConfigurationManager.AppSettings["BarCode"].ToString()));
      try
      {
        DtmfTerminatorDigit = Convert.ToChar(ConfigurationManager.AppSettings["DTMFTerminator"].ToString());
        BufferSize = Convert.ToInt32(ConfigurationManager.AppSettings["Buffer"].ToString());
      }
      catch (Exception err)
      {
        log.Error("No such configuration: DTMFTerminator + " + err.Message);
        DtmfTerminatorDigit = '#';
      }

      try
      {
        BufferSize = Convert.ToInt32(ConfigurationManager.AppSettings["Buffer"].ToString());
      }
      catch (Exception err)
      {
        log.Error("No such configuration: Buffer + " + err.Message);
        BufferSize = 8192;
      }

      try
      {
        RecvFormat = ConfigurationManager.AppSettings["ReceiveFormat"].ToString();
      }
      catch (Exception err)
      {
        log.Error("No such configuration: FaxReceiveFormat + " + err.Message);
        RecvFormat = "Default";
      }

      try
      {
        LogFileSize = ConfigurationManager.AppSettings["LogFileSize"].ToString();
      }
      catch (Exception err)
      {
        log.Error("No such configuration: LogFileSize + " + err.Message);
        LogFileSize = "10240";
      }

      try
      {
        LogFileCount = ConfigurationManager.AppSettings["LogFileCount"].ToString();
      }
      catch (Exception err)
      {
        log.Error("No such configuration: LogFileCount + " + err.Message);
        LogFileCount = "10";
      }

      //load etherfax config
      try
      {
        var etherFaxConfig = (NameValueCollection)ConfigurationManager.GetSection("Virtual/EtherFax");
        if (etherFaxConfig.Count > 1)
        {
          EtherFaxAddr = etherFaxConfig.Get("Address");
          EtherFaxAcc = etherFaxConfig.Get("Account");
          EtherFaxUsr = etherFaxConfig.Get("User");
          EtherFaxPwd = etherFaxConfig.Get("Password");
        }
      }
      catch (Exception err)
      {
        log.Error("Etherfax config section is not available.", err);
      }

      //load etherfax DR config
      try
      {
        NameValueCollection etherFaxDRConfig = (NameValueCollection)ConfigurationManager.GetSection("Virtual/EtherFaxDR");
        if (etherFaxDRConfig.Count > 1)
        {
          EtherFaxDRAgentID = etherFaxDRConfig.Get("AgentID");
          EtherFaxDRAddr = etherFaxDRConfig.Get("Address");
          EtherFaxDRAcc = etherFaxDRConfig.Get("Account");
          EtherFaxDRUsr = etherFaxDRConfig.Get("User");
          EtherFaxDRPwd = etherFaxDRConfig.Get("Password");
          EtherFaxDRCountryCode = etherFaxDRConfig.Get("CountryCode");
          EtherFaxDRAreaCode = etherFaxDRConfig.Get("AreaCode");
          EtherFaxDRRecvFormat = etherFaxDRConfig.Get("ReceiveFormat");
          EtherFaxDRSmartResume = Convert.ToBoolean(Convert.ToInt32(etherFaxDRConfig.Get("AutoResume").ToString()));
          EtherFaxDRAutoAdjReso = Convert.ToBoolean(Convert.ToInt32(etherFaxDRConfig.Get("AutoAdjustResolution").ToString()));
        }
      }
      catch (Exception errDR)
      {
        log.Error("EtherFax DR config section is not available.", errDR);
      }

      //load smart resume setting
      try
      {
        SmartResume = Convert.ToBoolean(Convert.ToInt32(ConfigurationManager.AppSettings["AutoResume"].ToString()));
      }
      catch (Exception err)
      {
        log.Error("Smart resume config section is not available. ", err);
        SmartResume = false;
      }

      //load auto adj resolution
      try
      {
        AutoAdjReso = Convert.ToBoolean(Convert.ToInt32(ConfigurationManager.AppSettings["AutoAdjustResolution"].ToString()));
      }
      catch (Exception err)
      {
        log.Error("AutoAdjustResolution config is not available. ", err);
        AutoAdjReso = false;
      }
      try
      {
        Interval = Convert.ToInt32(ConfigurationManager.AppSettings["Interval"].ToString());
      }
      catch (Exception err)
      {
        log.Error("Interval config is not  available.", err);
        Interval = 5000;
      }

      try
      {
        FaxTimeout = Convert.ToInt32(ConfigurationManager.AppSettings["FaxTimeout"].ToString());
      }
      catch (Exception err)
      {
        log.Error("FaxTimeout config is not available.", err);
        FaxTimeout = 60;
      }

      try
      {
        ChannelDelay = Convert.ToInt32(ConfigurationManager.AppSettings["ChannelDelay"].ToString());
      }
      catch (Exception err)
      {
        log.Error("ChannelDelay config is not available.", err);
      }

      try
      {
        IsRemoteAgent = Convert.ToBoolean(Convert.ToInt32(ConfigurationManager.AppSettings["RemoteAgent"].ToString()));
      }
      catch (Exception err)
      {
        log.Error("RemoteAgent config is not available.", err);
      }

      try
      {
        UdpListenPort = Convert.ToInt32(ConfigurationManager.AppSettings["BroadcastListenPort"].ToString());
      }
      catch (Exception err)
      {
        log.Error("BroadcastListenPort config is not available.", err);
        UdpListenPort = 11001;
      }
      log.Info("Agent ID:" + AgentId + " Server:" + Server + " Port:" + Port + " Log level:" + LogLevel +
        " Driver type:" + DriverType + " IniChannels:" + IniChannel + " Country code:" + CountryCode +
        " Area code:" + AreaCode + " PLK:" + Plk);

      try
      {
        var head = ConfigurationManager.AppSettings["ServerHost"].ToString();
        HostUrl = string.Format(@"{0}://{1}:{2}", head, Server, Port);
      }
      catch (Exception err)
      {
        log.ErrorFormat("ServerHost is not available. {0}", err.Message);
        HostUrl = "http";
      }

      try
      {
        ServiceKey = ConfigurationManager.AppSettings["api:service_key"].ToString();
      }
      catch (Exception err)
      {
        log.ErrorFormat("Service key is not available. {0}",err.Message);
        ServiceKey = string.Empty;
      }
    }
  }
}