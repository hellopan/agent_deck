package com.agentdeck.lab

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import org.json.JSONObject
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.util.Date
import java.util.UUID
import javax.security.auth.x500.X500Principal

internal data class PairingCandidate(val config: JSONObject, val device: String, val fingerprint: String)

internal fun requestPairing(raw: String): PairingCandidate {
    require(raw.length <= 4096)
    val invite = JSONObject(raw)
    require(invite.getInt("version") == 1)
    val device = invite.getString("device")
    val code = invite.getString("code")
    require(validDeviceID(device) && code.matches(Regex("[a-f0-9]{64}")))
    val alias = "agentdeck-phone-" + UUID.randomUUID()
    try {
        val now = System.currentTimeMillis()
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
            initialize(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
                .setDigests(KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA384, KeyProperties.DIGEST_SHA512)
                .setCertificateSubject(X500Principal("CN=AgentDeck Phone"))
                .setCertificateNotBefore(Date(now - 300000))
                .setCertificateNotAfter(Date(now + 365L * 86400000))
                .build())
        }.generateKeyPair()
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val fingerprint = MessageDigest.getInstance("SHA-256").digest(store.getCertificate(alias).encoded)
            .joinToString("") { "%02x".format(it.toInt() and 255) }
        val config = JSONObject().put("keyAlias", alias).put("relayUrl", invite.getString("relayUrl"))
            .put("relayCertificate", invite.getString("relayCertificate"))
            .put("devices", JSONObject().put(device, invite.getJSONObject("entry")))
        val reply = JSONObject(RelayClient(config, device, config.getString("relayUrl")).query("agentdeck-pair-v1 $code"))
        check(reply.optBoolean("pending")) { "电脑未接受配对请求" }
        return PairingCandidate(config, device, fingerprint)
    } catch (error: Exception) {
        KeyStore.getInstance("AndroidKeyStore").apply { load(null); deleteEntry(alias) }
        throw error
    }
}

internal fun validDeviceID(device: String) = device.matches(Regex("[ab]|[a-f0-9]{32}"))
