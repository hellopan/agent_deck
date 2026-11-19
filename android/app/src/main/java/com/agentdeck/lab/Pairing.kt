package com.agentdeck.lab

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import org.json.JSONException
import org.json.JSONObject
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.util.Date
import java.util.UUID
import javax.security.auth.x500.X500Principal

internal data class PairingCandidate(
    val config: JSONObject,
    val device: String,
    val fingerprint: String,
    val host: String,
)

/** What the QR code has to say, independent of which invite format carried it. */
private class Invite(
    val device: String,
    val code: String,
    val host: String,
    val relayUrl: String,
    /** Exactly one of these: the relay certificate itself (v1) or its key pin (v2). */
    val trustKey: String,
    val trustValue: String,
    val entry: JSONObject,
)

internal fun requestPairing(raw: String): PairingCandidate {
    require(raw.length <= 2048) { "配对码太长，可能扫错了二维码" }
    // A scanner will happily read any QR code in front of it. Anything that is
    // not our document has to come back as one readable sentence instead of a
    // JSONException naming a key the user has never heard of.
    val document = try {
        JSONObject(raw)
    } catch (error: JSONException) {
        throw IllegalArgumentException("这不是 AgentDeck 的配对码，请在电脑端「设置 → 配对」重新生成", error)
    }
    val invite = try {
        parseInvite(document)
    } catch (error: JSONException) {
        throw IllegalArgumentException("配对码内容不完整，请在电脑端重新生成", error)
    }
    val alias = "agentdeck-phone-" + UUID.randomUUID()
    try {
        val now = System.currentTimeMillis()
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
            initialize(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
                // Conscrypt signs the TLS 1.3 CertificateVerify through BoringSSL,
                // which hands the key an already-hashed digest (NONE). Without
                // DIGEST_NONE the Keystore answers INCOMPATIBLE_DIGEST and the
                // handshake dies with a bare SSL read error.
                .setDigests(
                    KeyProperties.DIGEST_NONE,
                    KeyProperties.DIGEST_SHA256,
                    KeyProperties.DIGEST_SHA384,
                    KeyProperties.DIGEST_SHA512,
                )
                .setCertificateSubject(X500Principal("CN=AgentDeck Phone"))
                .setCertificateNotBefore(Date(now - 300000))
                .setCertificateNotAfter(Date(now + 365L * 86400000))
                .build())
        }.generateKeyPair()
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val fingerprint = MessageDigest.getInstance("SHA-256").digest(store.getCertificate(alias).encoded)
            .joinToString("") { "%02x".format(it.toInt() and 255) }
        val config = JSONObject().put("keyAlias", alias).put("relayUrl", invite.relayUrl)
            .put(invite.trustKey, invite.trustValue)
            .put("devices", JSONObject().put(invite.device, invite.entry))
        val reply = JSONObject(RelayClient(config, invite.device, invite.relayUrl).query("agentdeck-pair-v1 " + invite.code))
        check(reply.optBoolean("pending")) { "电脑未接受配对请求" }
        return PairingCandidate(config, invite.device, fingerprint, invite.host)
    } catch (error: Exception) {
        KeyStore.getInstance("AndroidKeyStore").apply { load(null); deleteEntry(alias) }
        throw error
    }
}

/** Drops the key of a pairing that was superseded before it finished. */
internal fun discardPairing(config: JSONObject) {
    runCatching {
        KeyStore.getInstance("AndroidKeyStore").apply {
            load(null)
            deleteEntry(config.getString("keyAlias"))
        }
    }
}

/**
 * Two formats are accepted on purpose. A desktop that has not been updated yet
 * still prints the v1 invite with two PEM certificates, and it should keep
 * working; the current desktop prints v2, where each certificate is replaced by
 * the SHA-256 of its public key. Same authority, 336 bytes instead of 1640 —
 * which is the difference between a version 12 code (65 modules across) and a
 * code so dense (149) that a phone camera could not resolve it.
 */
private fun parseInvite(invite: JSONObject): Invite {
    if (invite.has("v")) {
        require(invite.getInt("v") == 2) { "配对码版本太新，请把手机应用也升级到最新版" }
        val device = invite.getString("d")
        val code = invite.getString("c")
        val url = invite.getString("u")
        val token = invite.getString("t")
        val relayPin = invite.getString("rp")
        val peerPin = invite.getString("pp")
        require(validDeviceID(device)) { "配对码里的设备 ID 无效" }
        require(code.matches(HEX_32_BYTES)) { "配对码格式不对" }
        require(token.matches(HEX_32_BYTES)) { "配对码里的路由凭据无效" }
        require(relayPin.isPin() && peerPin.isPin()) { "配对码里的证书指纹无效" }
        require(url.startsWith("wss://")) { "配对码里的 Relay 地址无效" }
        return Invite(
            device = device,
            code = code,
            host = invite.optString("h").take(64),
            relayUrl = url,
            trustKey = "relayPin",
            trustValue = relayPin,
            entry = JSONObject().put("token", token).put("pin", peerPin),
        )
    }
    require(invite.has("version")) { "这不是 AgentDeck 的配对码" }
    require(invite.getInt("version") == 1) { "配对码版本不受支持，请把电脑端升级到最新版" }
    val device = invite.getString("device")
    val code = invite.getString("code")
    require(validDeviceID(device) && code.matches(HEX_32_BYTES)) { "配对码格式不对" }
    return Invite(
        device = device,
        code = code,
        host = invite.optString("host").take(64),
        relayUrl = invite.getString("relayUrl"),
        trustKey = "relayCertificate",
        trustValue = invite.getString("relayCertificate"),
        entry = invite.getJSONObject("entry"),
    )
}

/** 32 random bytes, base64url, no padding: a SHA-256 pin is always 43 characters. */
private fun String.isPin() = length == 43 && matches(Regex("[A-Za-z0-9_-]+"))

private val HEX_32_BYTES = Regex("[a-f0-9]{64}")

internal fun validDeviceID(device: String) = device.matches(Regex("[ab]|[a-f0-9]{32}"))
