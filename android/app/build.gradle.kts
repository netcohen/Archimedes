plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    // Firebase Google Services plugin is NOT applied here.
    // Firebase is handled server-side via the Net service (firebase-admin).
    // To enable FCM push notifications on Android, add google-services.json and apply the plugin.
}
android {
    namespace = "com.archimedes.app"
    compileSdk = 34
    defaultConfig {
        applicationId = "com.archimedes.app"
        minSdk = 26
        targetSdk = 34
        versionCode = 2
        versionName = "0.2.0"
    }
    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        viewBinding = true
    }
}
dependencies {
    // Core Android
    implementation("androidx.core:core-ktx:1.12.0")
    implementation("androidx.appcompat:appcompat:1.6.1")
    implementation("com.google.android.material:material:1.11.0")
    implementation("androidx.constraintlayout:constraintlayout:2.1.4")
    implementation("androidx.cardview:cardview:1.0.0")
    implementation("androidx.recyclerview:recyclerview:1.3.2")

    // Background polling
    implementation("androidx.work:work-runtime-ktx:2.9.0")

    // Coroutines
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.7.3")

    // QR scanning (Phase 0 pairing)
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
}
