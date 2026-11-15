package com.agentdeck.lab

import android.util.Base64
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.URI
import java.nio.ByteBuffer
import java.security.Principal
import java.security.PrivateKey
import java.security.KeyStore
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.net.Socket
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLEngine
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngineResult.HandshakeStatus
import javax.net.ssl.SSLEngineResult.Status
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager

internal class RelayClient(config: JSONObject, private val device: String, endpoint: String) {
    private val entry = config.getJSONObject("devices").getJSONObject(device)
    private val outerTrust = trust(config.getString("relayCertificate"))
    private val outer = SSLContext.getInstance("TLS").apply { init(null, arrayOf(outerTrust), null) }
    private val inner = SSLContext.getInstance("TLSv1.3").apply {
        val alias = config.getString("keyAlias")
        val keystore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val certificate = keystore.getCertificate(alias) as X509Certificate
        val key = keystore.getKey(alias, null) as PrivateKey
        val keys = object : X509ExtendedKeyManager() {
            override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? =
                if (keyType == "EC") arrayOf(alias) else null
            override fun chooseClientAlias(types: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?): String? =
                if (types?.contains("EC") == true) alias else null
            override fun chooseEngineClientAlias(types: Array<out String>?, issuers: Array<out Principal>?, engine: SSLEngine?): String? =
                chooseClientAlias(types, issuers, null)
            override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? = null
            override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?): String? = null
            override fun getCertificateChain(requestedAlias: String?): Array<X509Certificate>? =
                if (requestedAlias == alias) arrayOf(certificate) else null
            override fun getPrivateKey(requestedAlias: String?): PrivateKey? = if (requestedAlias == alias) key else null
        }
        init(arrayOf(keys), arrayOf(trust(entry.getString("peerCertificate"))), null)
    }
    private val origin = URI(endpoint.trim()).also {
        require(it.scheme == "wss" && it.host != null && it.userInfo == null &&
            it.rawQuery == null && it.rawFragment == null && it.path in listOf("", "/")) {
            "Relay 地址须为 wss:// 主机和端口"
        }
        require(validDeviceID(device)) { "设备 ID 无效" }
    }

    fun query(command: String): String {
        val queue = ArrayBlockingQueue<ByteArray>(32)
        val closed = ByteArray(0)
        val client = OkHttpClient.Builder().sslSocketFactory(outer.socketFactory, outerTrust)
            .followRedirects(false).followSslRedirects(false).build()
        val request = Request.Builder().url(origin.toString().trimEnd('/') + "/v1/phone/" + device)
            .header("Authorization", "Bearer " + entry.getString("token")).build()
        val ws = client.newWebSocket(request, object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                if (bytes.size !in 1..65536 || !queue.offer(bytes.toByteArray())) {
                    queue.clear()
                    queue.offer(closed)
                    webSocket.cancel()
                }
            }
            override fun onMessage(webSocket: WebSocket, text: String) {
                queue.offer(closed)
                webSocket.cancel()
            }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                queue.offer(closed)
            }
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                queue.offer(closed)
                webSocket.close(code, null)
            }
        })
        try {
            val engine = inner.createSSLEngine(entry.getString("peerName"), 443).apply {
                useClientMode = true
                enabledProtocols = arrayOf("TLSv1.3")
                sslParameters = sslParameters.apply { endpointIdentificationAlgorithm = "HTTPS" }
                beginHandshake()
            }
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(12)
            val send = ByteBuffer.wrap((command + "\n").toByteArray(Charsets.UTF_8))
            val empty = ByteBuffer.allocate(0)
            val encryptedIn = ByteBuffer.allocate(128 * 1024).apply { limit(0) }
            val encryptedOut = ByteBuffer.allocate(64 * 1024)
            val clear = ByteBuffer.allocate(64 * 1024)
            val result = ByteArrayOutputStream()
            var needFrame = true
            while (System.nanoTime() < deadline) {
                val handshake = engine.handshakeStatus
                when {
                    handshake == HandshakeStatus.NEED_TASK -> {
                        var task = engine.delegatedTask
                        while (task != null) {
                            task.run()
                            task = engine.delegatedTask
                        }
                    }
                    handshake == HandshakeStatus.NEED_WRAP ||
                        (handshake == HandshakeStatus.NOT_HANDSHAKING && send.hasRemaining()) -> {
                        encryptedOut.clear()
                        val wrap = engine.wrap(if (handshake == HandshakeStatus.NEED_WRAP) empty else send, encryptedOut)
                        if (wrap.status != Status.OK) throw IOException("无法发送 TLS 数据")
                        if (encryptedOut.position() != 0 &&
                            !ws.send(encryptedOut.array().toByteString(0, encryptedOut.position()))) {
                            throw IOException("Relay 已断开")
                        }
                    }
                    else -> {
                        if (needFrame || !encryptedIn.hasRemaining()) {
                            val remaining = (deadline - System.nanoTime()).coerceAtLeast(0)
                            val frame = queue.poll(remaining, TimeUnit.NANOSECONDS)
                            if (frame == null || frame.isEmpty()) throw IOException("Relay 未返回结果或连接中断")
                            encryptedIn.compact()
                            if (frame.size > encryptedIn.remaining()) throw IOException("TLS 数据过大")
                            encryptedIn.put(frame).flip()
                        }
                        clear.clear()
                        val unwrap = engine.unwrap(encryptedIn, clear)
                        needFrame = unwrap.status == Status.BUFFER_UNDERFLOW ||
                            (unwrap.bytesConsumed() == 0 && unwrap.bytesProduced() == 0 &&
                                engine.handshakeStatus == HandshakeStatus.NOT_HANDSHAKING)
                        if (unwrap.status != Status.OK && unwrap.status != Status.BUFFER_UNDERFLOW)
                            throw IOException("内部 TLS 连接关闭")
                        if (result.size() + clear.position() > 3 * 1024 * 1024) throw IOException("响应过大")
                        for (i in 0 until clear.position()) {
                            val byte = clear.get(i)
                            if (byte == 10.toByte()) return result.toByteArray().toString(Charsets.UTF_8)
                            result.write(byte.toInt())
                        }
                    }
                }
            }
            throw IOException("Relay 请求超时")
        } finally {
            ws.cancel()
            client.dispatcher.executorService.shutdown()
            client.connectionPool.evictAll()
        }
    }

    private companion object {
        fun pem(value: String, label: String): ByteArray = Base64.decode(
            value.replace("-----BEGIN $label-----", "").replace("-----END $label-----", ""), Base64.DEFAULT)

        fun certificate(value: String): X509Certificate =
            CertificateFactory.getInstance("X.509").generateCertificate(
                pem(value, "CERTIFICATE").inputStream()) as X509Certificate

        fun trust(value: String): X509TrustManager {
            val store = KeyStore.getInstance(KeyStore.getDefaultType()).apply {
                load(null)
                setCertificateEntry("peer", certificate(value))
            }
            return TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm()).run {
                init(store)
                trustManagers.filterIsInstance<X509TrustManager>().single()
            }
        }
    }
}
