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
import java.security.MessageDigest
import java.security.Principal
import java.security.PrivateKey
import java.security.KeyStore
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.net.Socket
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.SSLEngine
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngineResult.HandshakeStatus
import javax.net.ssl.SSLEngineResult.Status
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager

internal class RelayClient(config: JSONObject, private val device: String, endpoint: String) {
    private val entry = config.getJSONObject("devices").getJSONObject(device)
    private val peerName = entry.optString("peerName").takeIf { it.isNotEmpty() } ?: "$device.agentdeck.test"
    private val outerTrust = trustFor(config, "relayPin", "relayCertificate")
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
        init(arrayOf(keys), arrayOf(trustFor(entry, "pin", "peerCertificate", peerName)), null)
    }
    private val origin = URI(endpoint.trim()).also {
        require(it.scheme == "wss" && it.host != null && it.userInfo == null &&
            it.rawQuery == null && it.rawFragment == null && it.path in listOf("", "/")) {
            "Relay 地址须为 wss:// 主机和端口"
        }
        require(validDeviceID(device)) { "设备 ID 无效" }
    }

    fun query(command: String): String {
        var attempt = 0
        while (true) {
            try {
                return queryOnce(command)
            } catch (error: IOException) {
                // The Relay keeps one tunnel per device and the Agent needs a
                // beat to re-attach after the previous request, so the second of
                // two back-to-back requests often lands in that gap. A 503 means
                // the upgrade never happened and nothing was delivered, which
                // makes a short retry safe even for mutations.
                val gap = error.message?.contains("503") == true
                if (!gap || attempt >= 3) throw error
                attempt += 1
                Thread.sleep(300L * attempt)
            }
        }
    }

    private fun queryOnce(command: String): String {
        val queue = ArrayBlockingQueue<ByteArray>(32)
        val closed = ByteArray(0)
        val failure = AtomicReference<Throwable?>(null)
        val upgraded = AtomicBoolean(false)
        val started = System.currentTimeMillis()
        // Records what the client was doing when it gave up. Without this the
        // only thing a user can report is the innermost SSL message, which says
        // nothing about whether the tunnel, the handshake or the reply failed.
        var stage = "拨号 Relay"
        val client = OkHttpClient.Builder().sslSocketFactory(outer.socketFactory, outerTrust)
            .followRedirects(false).followSslRedirects(false).build()
        val request = Request.Builder().url(origin.toString().trimEnd('/') + "/v1/phone/" + device)
            .header("Authorization", "Bearer " + entry.getString("token")).build()
        val ws = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                upgraded.set(true)
                stage = "等待握手响应"
            }
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
                // Keep the reason instead of collapsing it into "connection
                // lost": a rejected upgrade (401/503) and a TLS failure read
                // very differently in the field.
                val reason = StringBuilder(t.javaClass.simpleName)
                t.message?.takeIf { it.isNotBlank() }?.let { reason.append(": ").append(it) }
                response?.let { reason.append(" [HTTP ").append(it.code).append("]") }
                failure.compareAndSet(null, IOException(reason.toString(), t))
                queue.offer(closed)
            }
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                failure.compareAndSet(null, IOException("Relay 关闭连接 [$code] $reason".trim()))
                queue.offer(closed)
                webSocket.close(code, null)
            }
        })
        try {
            stage = "构造内部 TLS"
            val engine = inner.createSSLEngine(peerName, 443).apply {
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
                        stage = if (handshake == HandshakeStatus.NEED_WRAP) "内部 TLS 握手" else "发送请求"
                        encryptedOut.clear()
                        val wrap = engine.wrap(if (handshake == HandshakeStatus.NEED_WRAP) empty else send, encryptedOut)
                        if (wrap.status != Status.OK) throw IOException("无法发送 TLS 数据")
                        if (encryptedOut.position() != 0 &&
                            !ws.send(encryptedOut.array().toByteString(0, encryptedOut.position()))) {
                            throw IOException("Relay 已断开")
                        }
                    }
                    else -> {
                        stage = "等待 Relay 回包"
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
        } catch (error: Exception) {
            throw describe(stage, error, failure.get(), upgraded.get(), System.currentTimeMillis() - started)
        } finally {
            ws.cancel()
            client.dispatcher.executorService.shutdown()
            client.connectionPool.evictAll()
        }
    }

    private companion object {
        /** Folds the stage, the outer WebSocket reason and the elapsed time into
         *  one line: the phone can only send text back, so the error itself has
         *  to carry enough context to tell a 401 from a TLS failure. */
        fun describe(stage: String, error: Exception, outer: Throwable?, upgraded: Boolean, elapsed: Long): IOException {
            val text = StringBuilder(stage).append(" 失败：")
            text.append(error.message.orEmpty().ifBlank { error.javaClass.simpleName })
            if (outer != null && outer.message != error.message) {
                text.append("；外层 ").append(outer.javaClass.simpleName)
                outer.message?.takeIf { it.isNotBlank() }?.let { text.append(": ").append(it) }
            }
            text.append("（").append(if (upgraded) "WebSocket 已建立" else "WebSocket 未建立")
                .append("，").append(elapsed).append(" ms）")
            return IOException(text.toString(), error)
        }

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

        /** v2 invites carry a pin instead of a PEM; v1 invites still carry the
         *  certificate, and both paths have to keep working. */
        fun trustFor(source: JSONObject, pinKey: String, pemKey: String, expectName: String? = null): X509TrustManager {
            val pin = source.optString(pinKey)
            return if (pin.isNotEmpty()) pinned(pin, expectName) else trust(source.getString(pemKey))
        }

        /**
         * Trusts the one key the QR named, not "anything a CA signed". Checking
         * the SPKI hash is what the certificate chain check would have
         * established, with the certificate itself left out of the QR code.
         *
         * A custom manager replaces the platform's, and the platform's is also
         * what normally enforces endpoint identification — so the name check
         * that [SSLEngine.setSSLParameters] asked for is done here instead.
         */
        fun pinned(value: String, expectName: String?): X509TrustManager {
            val bytes = Base64.decode(value, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
            require(bytes.size == 32) { "配对码里的证书指纹无效" }
            return object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                    throw CertificateException("这只用于验证电脑")
                }

                override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                    val leaf = chain?.firstOrNull() ?: throw CertificateException("电脑没有出示证书")
                    leaf.checkValidity()
                    if (expectName != null && !leaf.matchesName(expectName)) {
                        throw CertificateException("电脑证书不属于 " + expectName)
                    }
                    val actual = MessageDigest.getInstance("SHA-256").digest(leaf.publicKey.encoded)
                    if (!MessageDigest.isEqual(actual, bytes)) throw CertificateException("电脑证书与配对码不符")
                }

                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            }
        }

        fun X509Certificate.matchesName(name: String): Boolean =
            subjectAlternativeNames?.any { entry ->
                entry.size >= 2 && (entry[0] as? Number)?.toInt() == 2 &&
                    name.equals(entry[1] as? String, ignoreCase = true)
            } == true
    }
}
