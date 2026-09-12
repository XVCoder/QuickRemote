package com.quickremote.app.data.models

/** 设备列表中的一行：设备 + 本机备注（备注仅本机可见）。 */
data class DeviceListItem(
    val device: Device,
    val remark: String = ""
)

/**
 * 装配结果：在线/离线两段，以及本次需要「解除隐藏」的设备 ID。
 *
 * [revived] 由调用方写回本地隐藏集合 —— 软删除的设备重新上线即自动恢复，
 * 这是与 PC 端一致的语义（pc-client ViewModels/MainViewModel.cs: OnDeviceListUpdated）。
 */
data class DeviceListResult(
    val online: List<DeviceListItem> = emptyList(),
    val offline: List<DeviceListItem> = emptyList(),
    val revived: Set<String> = emptySet()
) {
    val total: Int get() = online.size + offline.size
    val isEmpty: Boolean get() = total == 0
}

/**
 * 把服务端设备表（含离线）与本机私有状态（备注、隐藏集合）装配成展示列表。
 *
 * 规则（与 PC 端逐条一致，顺序不可调换）：
 * 1. device_id 为空的脏数据丢弃；
 * 2. 隐藏集合中的设备：在线 → 记为复活并照常展示；离线 → 跳过；
 * 3. 其余按 status 分入在线/离线两段，段内保持输入顺序（服务端已排序）。
 *
 * 为什么复活判定必须在前、且只对在线设备成立：若先过滤隐藏再判复活，软删除的设备
 * 永远回不来；若对离线设备也判复活，则「移除离线设备」这个功能会当场失效。
 */
fun assembleDeviceList(
    devices: List<Device>,
    remarks: Map<String, String> = emptyMap(),
    hidden: Set<String> = emptySet()
): DeviceListResult {
    val online = ArrayList<DeviceListItem>()
    val offline = ArrayList<DeviceListItem>()
    val revived = LinkedHashSet<String>()

    for (device in devices) {
        if (device.device_id.isBlank()) continue
        val isHidden = device.device_id in hidden
        if (device.isOnline) {
            if (isHidden) revived += device.device_id
        } else if (isHidden) {
            continue
        }
        val item = DeviceListItem(device, remarks[device.device_id].orEmpty())
        if (device.isOnline) online += item else offline += item
    }
    return DeviceListResult(online = online, offline = offline, revived = revived)
}
