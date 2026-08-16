package com.quickremote.app.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import com.quickremote.app.data.models.ServerConfig
import com.quickremote.app.ui.theme.Accent
import com.quickremote.app.ui.theme.BgCard
import com.quickremote.app.ui.theme.Border
import com.quickremote.app.ui.theme.BorderLight
import com.quickremote.app.ui.theme.Danger
import com.quickremote.app.ui.theme.Success
import com.quickremote.app.ui.theme.TextMuted
import com.quickremote.app.ui.theme.TextPrimary
import com.quickremote.app.ui.theme.TextSecondary
import com.quickremote.app.viewmodels.ConnectionState
import com.quickremote.app.viewmodels.MainViewModel

/**
 * 服务器配置页：输入服务器地址 + 预共享密钥，测试连接，保存并继续。
 * 首次启动自动展示。
 */
@Composable
fun ServerConfigScreen(
    viewModel: MainViewModel,
    onContinue: () -> Unit
) {
    val savedConfig by viewModel.serverConfig.collectAsState()
    val connectionState by viewModel.connectionState.collectAsState()
    val lastError by viewModel.lastError.collectAsState()

    var address by remember(savedConfig.address) { mutableStateOf(savedConfig.address) }
    var preSharedKey by remember(savedConfig.preSharedKey) { mutableStateOf(savedConfig.preSharedKey) }
    var keyVisible by remember { mutableStateOf(false) }

    val isTesting = connectionState == ConnectionState.CONNECTING
    val config = ServerConfig(address = address.trim(), preSharedKey = preSharedKey)
    val canSubmit = config.address.isNotBlank() && config.preSharedKey.isNotBlank() && !isTesting

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            .verticalScroll(rememberScrollState())
            .windowInsetsPadding(WindowInsets.statusBars)
            .padding(horizontal = 20.dp, vertical = 32.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        // Logo / 标题
        Box(
            modifier = Modifier
                .size(56.dp)
                .clip(RoundedCornerShape(14.dp))
                .background(BgCard)
                .border(1.dp, BorderLight, RoundedCornerShape(14.dp)),
            contentAlignment = Alignment.Center
        ) {
            Text("QR", color = Accent, fontWeight = FontWeight.Bold)
        }
        Spacer(modifier = Modifier.height(12.dp))
        Text("QuickRemote", style = MaterialTheme.typography.titleLarge, color = TextPrimary)
        Spacer(modifier = Modifier.height(4.dp))
        Text(
            "配置中转服务器以开始",
            style = MaterialTheme.typography.bodyMedium,
            color = TextSecondary
        )

        Spacer(modifier = Modifier.height(28.dp))

        // 表单卡片
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(8.dp))
                .background(BgCard)
                .border(1.dp, Border, RoundedCornerShape(8.dp))
                .padding(16.dp)
        ) {
            Text("服务器地址", style = MaterialTheme.typography.labelMedium, color = TextMuted)
            Spacer(modifier = Modifier.height(6.dp))
            OutlinedTextField(
                value = address,
                onValueChange = { address = it },
                placeholder = { Text("host:port", color = TextMuted) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(4.dp),
                colors = textFieldColors()
            )

            Spacer(modifier = Modifier.height(14.dp))

            Text("预共享密钥", style = MaterialTheme.typography.labelMedium, color = TextMuted)
            Spacer(modifier = Modifier.height(6.dp))
            OutlinedTextField(
                value = preSharedKey,
                onValueChange = { preSharedKey = it },
                placeholder = { Text("输入预共享密钥", color = TextMuted) },
                singleLine = true,
                visualTransformation = if (keyVisible) VisualTransformation.None else PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                trailingIcon = {
                    Text(
                        text = if (keyVisible) "隐藏" else "显示",
                        color = Accent,
                        fontWeight = FontWeight.Medium,
                        modifier = Modifier
                            .clickable { keyVisible = !keyVisible }
                            .padding(horizontal = 12.dp, vertical = 8.dp)
                    )
                },
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(4.dp),
                colors = textFieldColors()
            )
        }

        Spacer(modifier = Modifier.height(20.dp))

        // 连接状态提示
        when (connectionState) {
            ConnectionState.CONNECTED -> StatusLine("连接测试成功", Success)
            ConnectionState.ERROR -> ErrorStatusBlock(lastError)
            else -> {}
        }

        // 测试连接
        OutlinedButton(
            onClick = { viewModel.testConnection(config) },
            enabled = canSubmit,
            modifier = Modifier.fillMaxWidth().height(46.dp),
            shape = RoundedCornerShape(4.dp),
            border = androidx.compose.foundation.BorderStroke(1.dp, BorderLight),
            colors = ButtonDefaults.outlinedButtonColors(
                contentColor = TextPrimary,
                containerColor = BgCard
            )
        ) {
            if (isTesting) {
                CircularProgressIndicator(
                    modifier = Modifier.size(16.dp),
                    strokeWidth = 2.dp,
                    color = TextPrimary
                )
            } else {
                Text("测试连接", fontWeight = FontWeight.Medium)
            }
        }

        Spacer(modifier = Modifier.height(10.dp))

        // 保存并继续
        Button(
            onClick = {
                viewModel.saveAndContinue(config)
                onContinue()
            },
            enabled = config.address.isNotBlank() && config.preSharedKey.isNotBlank() && !isTesting,
            modifier = Modifier.fillMaxWidth().height(46.dp),
            shape = RoundedCornerShape(4.dp),
            colors = ButtonDefaults.buttonColors(
                containerColor = Accent,
                contentColor = TextPrimary
            )
        ) {
            Text("保存并继续", fontWeight = FontWeight.SemiBold)
        }

        Spacer(modifier = Modifier.height(24.dp))
        Text(
            "提示：密钥会以 SHA-256 哈希形式发送到服务器验证。",
            style = MaterialTheme.typography.bodySmall,
            color = TextMuted
        )
    }

    // 消费一次 toast
    ToastConsumer(viewModel)
}

@Composable
private fun StatusLine(text: String, color: androidx.compose.ui.graphics.Color) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center
    ) {
        Box(modifier = Modifier.size(8.dp).clip(RoundedCornerShape(4.dp)).background(color))
        Spacer(modifier = Modifier.size(8.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium, color = color)
    }
}

/** 错误状态块：显示"连接失败"标题 + 具体原因（多行可换行）。 */
@Composable
private fun ErrorStatusBlock(detail: String) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(bottom = 12.dp)
            .clip(RoundedCornerShape(6.dp))
            .background(Danger.copy(alpha = 0.08f))
            .border(1.dp, Danger.copy(alpha = 0.3f), RoundedCornerShape(6.dp))
            .padding(horizontal = 12.dp, vertical = 10.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(modifier = Modifier.size(8.dp).clip(RoundedCornerShape(4.dp)).background(Danger))
            Spacer(modifier = Modifier.size(8.dp))
            Text("连接失败", style = MaterialTheme.typography.bodyMedium, color = Danger, fontWeight = FontWeight.Medium)
        }
        if (detail.isNotBlank()) {
            Spacer(modifier = Modifier.height(6.dp))
            Text(
                detail,
                style = MaterialTheme.typography.bodySmall,
                color = TextMuted,
                modifier = Modifier.padding(start = 16.dp)
            )
        }
    }
}

@Composable
private fun ToastConsumer(viewModel: MainViewModel) {
    val toast by viewModel.toast.collectAsState()
    LaunchedEffect(toast) {
        if (toast != null) viewModel.consumeToast()
    }
}

@Composable
private fun textFieldColors() = TextFieldDefaults.colors(
    focusedContainerColor = MaterialTheme.colorScheme.background,
    unfocusedContainerColor = MaterialTheme.colorScheme.background,
    cursorColor = Accent,
    focusedIndicatorColor = Accent,
    unfocusedIndicatorColor = BorderLight,
    focusedTextColor = TextPrimary,
    unfocusedTextColor = TextPrimary,
    focusedPlaceholderColor = TextMuted,
    unfocusedPlaceholderColor = TextMuted
)


