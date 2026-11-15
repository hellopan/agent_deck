package com.agentdeck.lab

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import org.json.JSONObject
import java.io.FileNotFoundException
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

internal class LabState(context: Context) {
    private val file = AtomicFile(context.noBackupFilesDir.resolve("agentdeck-lab.enc"))
    private val alias = "agentdeck-lab-storage"

    private fun key(create: Boolean): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(alias, null) as? SecretKey)?.let { return it }
        check(create) { "设备密钥不可用，请重新导入配置。" }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())
            generateKey()
        }
    }

    fun read(): JSONObject? {
        val data = try {
            file.readFully()
        } catch (_: FileNotFoundException) {
            return null
        }
        require(data.size in 29..131072) { "本地配置格式错误" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(false), GCMParameterSpec(128, data, 0, 12))
        return JSONObject(cipher.doFinal(data, 12, data.size - 12).toString(Charsets.UTF_8))
    }

    fun write(state: JSONObject) {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key(true))
        check(cipher.iv.size == 12)
        val data = cipher.iv + cipher.doFinal(state.toString().toByteArray(Charsets.UTF_8))
        require(data.size <= 131072) { "配置过大" }
        val output = file.startWrite()
        try {
            output.write(data)
            file.finishWrite(output)
        } catch (error: Exception) {
            file.failWrite(output)
            throw error
        }
    }
}
