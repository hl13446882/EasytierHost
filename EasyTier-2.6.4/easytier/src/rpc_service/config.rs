use std::sync::Arc;

use crate::{
    instance_manager::NetworkInstanceManager,
    proto::{
        api::config::{
            ConfigRpc, GetConfigRequest, GetConfigResponse, PatchConfigRequest, PatchConfigResponse,
        },
        rpc_types::{self, controller::BaseController},
    },
};

#[derive(Clone)]
pub struct ConfigRpcService {
    instance_manager: Arc<NetworkInstanceManager>,
}

impl ConfigRpcService {
    pub fn new(instance_manager: Arc<NetworkInstanceManager>) -> Self {
        Self { instance_manager }
    }
}

#[async_trait::async_trait]
impl ConfigRpc for ConfigRpcService {
    type Controller = BaseController;

    async fn patch_config(
        &self,
        ctrl: Self::Controller,
        input: PatchConfigRequest,
    ) -> Result<PatchConfigResponse, rpc_types::error::Error> {
        // Host underlay mode is intentionally immutable. A live patch could re-enable an
        // unaudited transport/UPnP path after the client default route has moved into TUN.
        // Reconfiguration must therefore be expressed in the Host profile and applied by
        // stopping/restarting Core so the physical binding can be captured again.
        if crate::tunnel::underlay_policy::is_enabled() {
            return Err(anyhow::anyhow!(
                "config mutation is disabled while Host underlay protection is active; restart Core through EasyTierHost"
            )
            .into());
        }
        super::get_instance_service(&self.instance_manager, &input.instance)?
            .get_config_service()
            .patch_config(ctrl, input)
            .await
    }

    async fn get_config(
        &self,
        ctrl: Self::Controller,
        input: GetConfigRequest,
    ) -> Result<GetConfigResponse, rpc_types::error::Error> {
        super::get_instance_service(&self.instance_manager, &input.instance)?
            .get_config_service()
            .get_config(ctrl, input)
            .await
    }
}
