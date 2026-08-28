plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.questdetect.ar"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.questdetect.ar"
        // The Passthrough Camera API is Horizon OS v74+ / Android 14.
        minSdk = 34
        targetSdk = 34
        versionCode = 1
        versionName = "1.0"

        // Quest 3/3S is arm64 only; shipping the other ABIs of ONNX Runtime
        // costs ~55 MB of APK for architectures that can never run this.
        ndk {
            abiFilters += "arm64-v8a"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    // The ONNX graph is already compressed-ish and must be mmap-able from the APK.
    androidResources {
        noCompress += "onnx"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }

    packaging {
        resources.excludes += setOf("META-INF/*.kotlin_module")
    }
}

dependencies {
    implementation("com.microsoft.onnxruntime:onnxruntime-android:1.20.0")
}
