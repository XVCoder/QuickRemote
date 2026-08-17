package com.quickremote.app.ui.navigation

import androidx.compose.runtime.Composable
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.navigation.NavHostController
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import com.quickremote.app.data.local.SettingsStore
import com.quickremote.app.data.models.Device
import com.quickremote.app.services.LanDiscovery
import com.quickremote.app.ui.screens.DeviceListScreen
import com.quickremote.app.ui.screens.RdpSessionScreen
import com.quickremote.app.ui.screens.RemoteSessionScreen
import com.quickremote.app.ui.screens.ServerConfigScreen
import com.quickremote.app.ui.screens.SettingsScreen
import com.quickremote.app.viewmodels.MainViewModel
import com.quickremote.app.viewmodels.MainViewModelFactory
import com.quickremote.app.viewmodels.RdpSessionViewModel
import com.quickremote.app.viewmodels.RdpSessionViewModelFactory
import com.quickremote.app.viewmodels.SessionViewModel
import com.quickremote.app.viewmodels.SessionViewModelFactory
import android.app.Application
import android.net.Uri
import androidx.compose.ui.platform.LocalContext
import kotlinx.serialization.json.Json

/**
 * 页面路由常量。
 */
object Routes {
    const val SERVER_CONFIG = "server_config"
    const val DEVICE_LIST = "device_list"
    const val REMOTE_SESSION = "remote_session/{device}"
    const val LAN_RDP_SESSION = "lan_rdp_session/{lan}"
    const val SETTINGS = "settings"

    fun remoteSession(device: String) = "remote_session/${Uri.encode(device)}"
    fun lanRdpSession(lan: String) = "lan_rdp_session/${Uri.encode(lan)}"
}

/**
 * 应用导航图。
 *
 * 首次启动根据是否已配置服务器配置决定起始页面：
 * - 未配置 → server_config
 * - 已配置 → device_list
 */
@Composable
fun NavGraph(
    settingsStore: SettingsStore,
    startConfigured: Boolean
) {
    val navController: NavHostController = rememberNavController()

    NavHost(
        navController = navController,
        startDestination = if (startConfigured) Routes.DEVICE_LIST else Routes.SERVER_CONFIG
    ) {
        composable(Routes.SERVER_CONFIG) {
            val viewModel: MainViewModel = viewModel(
                factory = MainViewModelFactory(settingsStore)
            )
            ServerConfigScreen(
                viewModel = viewModel,
                onContinue = {
                    navController.navigate(Routes.DEVICE_LIST) {
                        popUpTo(Routes.SERVER_CONFIG) { inclusive = true }
                    }
                }
            )
        }

        composable(Routes.DEVICE_LIST) {
            val viewModel: MainViewModel = viewModel(
                factory = MainViewModelFactory(settingsStore)
            )
            DeviceListScreen(
                viewModel = viewModel,
                onDeviceClick = { device ->
                    val encoded = Json.encodeToString(Device.serializer(), device)
                    navController.navigate(Routes.remoteSession(encoded))
                },
                onLanDeviceClick = { lan ->
                    val encoded = Json.encodeToString(LanDiscovery.LanDevice.serializer(), lan)
                    navController.navigate(Routes.lanRdpSession(encoded))
                },
                onSettingsClick = { navController.navigate(Routes.SETTINGS) }
            )
        }

        // 内网 RDP 会话（局域网直连，FreeRDP + 凭据）
        composable(
            route = Routes.LAN_RDP_SESSION,
            arguments = listOf(navArgument("lan") { type = NavType.StringType })
        ) { backStackEntry ->
            val lanJson = backStackEntry.arguments?.getString("lan").orEmpty()
            val decoded = Uri.decode(lanJson)
            val lan = Json.decodeFromString(LanDiscovery.LanDevice.serializer(), decoded)
            val app = LocalContext.current.applicationContext as Application
            val viewModel: RdpSessionViewModel = viewModel(
                factory = RdpSessionViewModelFactory(app, settingsStore, lan)
            )
            RdpSessionScreen(
                device = lan.toDevice(),
                viewModel = viewModel,
                onBack = { navController.popBackStack() }
            )
        }

        composable(
            route = Routes.REMOTE_SESSION,
            arguments = listOf(navArgument("device") { type = NavType.StringType })
        ) { backStackEntry ->
            val deviceJson = backStackEntry.arguments?.getString("device").orEmpty()
            val decoded = Uri.decode(deviceJson)
            val device = Json.decodeFromString(Device.serializer(), decoded)
            val app = LocalContext.current.applicationContext as Application
            val viewModel: SessionViewModel = viewModel(
                factory = SessionViewModelFactory(app, settingsStore)
            )
            RemoteSessionScreen(
                device = device,
                viewModel = viewModel,
                onBack = { navController.popBackStack() }
            )
        }

        composable(Routes.SETTINGS) {
            val viewModel: MainViewModel = viewModel(
                factory = MainViewModelFactory(settingsStore)
            )
            SettingsScreen(
                viewModel = viewModel,
                onBack = { navController.popBackStack() }
            )
        }
    }
}
