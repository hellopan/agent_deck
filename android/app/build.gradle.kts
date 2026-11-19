import java.util.Properties

plugins {
    id("com.android.application") version "8.11.1"
    id("org.jetbrains.kotlin.android") version "2.1.21"
    id("org.jetbrains.kotlin.plugin.compose") version "2.1.21"
}

// The release key lives outside git. Without it the release build still works
// (unsigned), which keeps CI and fresh clones from breaking on a missing file.
val keystorePropertiesFile = rootProject.file("keystore.properties")
val keystoreProperties = Properties()
if (keystorePropertiesFile.exists()) {
    keystoreProperties.load(keystorePropertiesFile.inputStream())
}

android {
    namespace = "com.agentdeck.lab"
    compileSdk = 36
    defaultConfig {
        applicationId = "com.agentdeck.lab"
        minSdk = 29
        targetSdk = 36
        // Bumped with the three-page redesign so an install over the old build
        // is recognisable on the phone instead of looking like the same app.
        // 5: about page with the project's GitHub address.
        versionCode = 5
        versionName = "0.2.4-lab"
    }
    val releaseSigning = if (keystorePropertiesFile.exists()) {
        signingConfigs.create("release") {
            storeFile = file(keystoreProperties.getProperty("storeFile"))
            storePassword = keystoreProperties.getProperty("storePassword")
            keyAlias = keystoreProperties.getProperty("keyAlias")
            keyPassword = keystoreProperties.getProperty("keyPassword")
        }
    } else null
    buildTypes {
        release {
            signingConfig = releaseSigning
            isMinifyEnabled = false
        }
    }
    // Release builds are produced on an offline machine; lintVital would try
    // to resolve lint-gradle from the network and fail the whole assemble.
    lint {
        checkReleaseBuilds = false
        abortOnError = false
    }
    // VERSION_NAME is shown on the about page; AGP 8 does not generate
    // BuildConfig unless it is asked for.
    buildFeatures { compose = true; buildConfig = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
kotlin { jvmToolchain(17) }

dependencies {
    implementation(platform("androidx.compose:compose-bom:2025.05.01"))
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
}
