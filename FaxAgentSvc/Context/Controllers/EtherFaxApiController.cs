namespace FaxAgentSvc.Context.Controllers
{
    using FXC6.Entity.Api;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Web.Http;

    [FaxAgentAuthorize]
    [RoutePrefix("api/etherfax")]
    public class EtherFaxApiController : ApiController
    {
        private readonly EtherFaxHostContext _faxServer;

        public EtherFaxApiController(EtherFaxHostContext faxServer)
        {
            _faxServer = faxServer;
        }

        #region Action methods declared in Documentation
        ///// <summary>
        ///// Return current state of specific channels. Payload include fax channel status event
        ///// </summary>
        ///// <returns></returns>
        //[HttpGet]
        //[Route("channel_status")]
        //public async Task<IHttpActionResult> RetrieveChannelStatus()
        //{
        //    return Ok();
        //}

        ///// <summary>
        ///// update send/receive setting
        ///// set port enable/disable
        ///// set channel's configuration, ie CSID, header value, etc.
        ///// </summary>
        ///// <returns></returns>
        //[HttpPut]
        //[Route("update_channel")]
        //public async Task<IHttpActionResult> UpdateChannelConfig()
        //{
        //    return Ok();
        //}

        ///// <summary>
        ///// cancel message in transmission. Payload include agent ID, module and channel no.
        ///// channel count is zero based index.
        ///// </summary>
        ///// <returns></returns>
        //[HttpPost]
        //[Route("cancel_message")]
        //public async Task<IHttpActionResult> CancelMessage()
        //{
        //    return Ok();
        //} 
        #endregion

        [HttpGet, Route("ModuleStatus")]
        public async Task<IHttpActionResult> ModuleStatus()
        {
            return Json(_faxServer.RetrieveModuleInfo());
        }


        [HttpGet, Route("Utilization")]
        public async Task<IHttpActionResult> Utilization()
        {
            var model = new ChannelUtilizationResponse
            {
                Value = _faxServer.GetFSUtilization()
            };
            return Json(model);
        }

        [HttpGet, Route("FaxServerState")]
        public async Task<IHttpActionResult> FaxServerState()
        {
            var model = new FaxServerStatusResponse
            {
                Value = _faxServer.GetFSIsStarted()
            };
            return Json(model);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("ConfigureAnswerRings")]
        public async Task<IHttpActionResult> ConfigureAnswerRings(AnswerRingsRequestModel model)
        {
            _faxServer.ConfigureAnswerRings(model.Channel, model.Value);
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("ConfigureDialTimeout")]
        public async Task<IHttpActionResult> ConfigureDialTimeout(DialTimeoutRequestModel model)
        {
            _faxServer.ConfigureDialTimeout(model.Channel, model.Value);
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("ConfigureEnableChan")]
        public async Task<IHttpActionResult> ConfigureEnableChan(EnableChanRequestModel model)
        {
            _faxServer.ConfigureEnableChan(model.Channel, model.Value);
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="value"></param>
        /// <param name="customHeader"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("ConfigureHeaderStyle")]
        public async Task<IHttpActionResult> ConfigureHeaderStyle(HeaderStyleRequestModel model)
        {
            _faxServer.ConfigureHeaderStyle(model.Channel, model.Value, model.CustomHeader);
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("ConfigureLocalID")]
        public async Task<IHttpActionResult> ConfigureLocalID(LocalIDRequestModel model)
        {
            _faxServer.ConfigureLocalID(model.Channel, model.Value);
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("ConfigureReceiveEnabled")]
        public async Task<IHttpActionResult> ConfigureReceiveEnabled(ReceiveEnabledRequestModel model)
        {
            _faxServer.ConfigureReceiveEnabled(model.Channel, model.Value);
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("ConfigureSendEnabled")]
        public async Task<IHttpActionResult> ConfigureSendEnabled(SendEnabledRequestModel model)
        {
            _faxServer.ConfigureSendEnabled(model.Channel, model.Value);
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("ConfigureToneDetectEnabled")]
        public async Task<IHttpActionResult> ConfigureToneDetectEnabled(ToneDetectEnabledRequestModel model)
        {
            _faxServer.ConfigureToneDetectEnabled(model.Channel, model.Value);
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("ConfigureToneDetectDigits")]
        public async Task<IHttpActionResult> ConfigureToneDetectDigits(ToneDetectDigitsRequestModel model)
        {
            _faxServer.ConfigureToneDetectDigits(model.Channel, model.Value);
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("ConfigureDTMFToneType")]
        public async Task<IHttpActionResult> ConfigureDTMFToneType(DTMFToneTypeRequestModel model)
        {
            _faxServer.ConfigureDTMFToneType(model.Channel, model.Value);
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("ConfigureDTMFToneWav")]
        public async Task<IHttpActionResult> ConfigureDTMFToneWav(DTMFToneWavRequestModel model)
        {
            _faxServer.ConfigureDTMFToneWav(model.Channel, model.Value);
            return Ok();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("CancelFax")]
        public async Task<IHttpActionResult> CancelFax(CancelFaxRequestModel model)
        {
            _faxServer.CancelFax(model.Channel);
            return Ok();
        }

        [HttpPost, Route("FaxChannelSaving")]
        public async Task<IHttpActionResult> FaxChannelSave(FaxChannelSavingRequestModel model)
        {
            return await InternalChannelSave(new FaxChannelSavingRequestModel[] { model });
        }

        [HttpPost, Route("BulkSaveChannels")]
        public async Task<IHttpActionResult> BulkChannelSave(IEnumerable<FaxChannelSavingRequestModel> model)
        {
            return await InternalChannelSave(model);
        }

        [HttpPost, Route("BulkFaxAgentSave")]
        public async Task<IHttpActionResult> BulkFaxAgentSave(IEnumerable<FaxAgentSaveRequestModel> model)
        {
            return await InternalFaxAgentSave(model);
        }

        private async Task<IHttpActionResult> InternalFaxAgentSave(IEnumerable<FaxAgentSaveRequestModel> models)
        {
            if (models != null && models.Any())
            {
                foreach (var model in models)
                {
                    _faxServer.ConfigureAnswerRings(model.Channel, model.AnswerRingsValue);
                    _faxServer.ConfigureDialTimeout(model.Channel, model.DialTimeoutValue);
                    _faxServer.ConfigureToneDetectDigits(model.Channel, model.ToneDetectDigitsValue);
                    _faxServer.ConfigureLocalID(model.Channel, model.LocalIDValue);
                    _faxServer.ConfigureHeaderStyle(model.Channel, model.CustomHeader, model.HeaderStyleValue);
                    _faxServer.ConfigureDTMFToneType(model.Channel, model.DTMFToneTypeValue);
                    _faxServer.ConfigureDTMFToneWav(model.Channel, model.DTMFToneWavValue);
                }
            }
            return Ok();
        }

        private async Task<IHttpActionResult> InternalChannelSave(IEnumerable<FaxChannelSavingRequestModel> models)
        {
            if (models != null && models.Any())
            {
                foreach (var model in models)
                {
                    _faxServer.ConfigureReceiveEnabled(model.Channel, model.ReceiveEnabled);
                    _faxServer.ConfigureSendEnabled(model.Channel, model.SendEnabled);
                    _faxServer.ConfigureToneDetectEnabled(model.Channel, model.ToneDetectEnabled);
                    _faxServer.ConfigureEnableChan(model.Channel, model.ChannelEnabled);
                }
            }
            return Ok();
        }
    }
}
