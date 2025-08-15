namespace FaxAgentSvc
{
    internal class Api
    {
        internal const string Base = "/api/faxagent";
        internal class Post
        {
            internal const string RegisterFaxChannel = Base + "/register_channel";
            internal const string RegisterFaxAgent = Base + "/register";
            internal const string Outbound = Base + "/outbound";
            internal const string Incoming = Base + "/incoming";
            internal const string RescheduleQueue = Base + "/reschedule_queue";
        }

        internal class Get
        {
            internal const string GetImageQueue = Base + "/imageq";
            internal const string GetMessageQueue = Base + "/messageq";
            internal const string GetFaxConfig = Base + "/channel_configs";
      internal const string GetChannelQueue = Base + "/countq";
        }

        internal class Put
        {
            internal const string InvokeFaxChannels = Base + "/invoke_channel";
            internal const string InvokeFaxAgent = Base + "/invoke";
            internal const string UpdateChannelStatus = Base + "/channel_status";
            internal const string UpdateFaxAgentConfig = Base + "/agent_configs";
            internal const string UpdateFaxAgentStatus = Base + "/agent_config_status";
            //internal const string ResetQueue = Base + "/reset_queue";
        }
    }
}
