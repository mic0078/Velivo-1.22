plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// 💬 Komunikator Velivo - osobna aplikacja (ten sam klucz podpisu co Velivo); numer wersji jak w Velivo.
// Kod komunikatora (src/main/java) jest wspólny z przeglądarką i klawiaturą; src/app/java - tylko ta aplikacja.
val zmiany = runCatching { providers.exec { commandLine("git", "rev-list", "--count", "HEAD") }.standardOutput.asText.get().trim().toInt() }.getOrDefault(2)

android {
    namespace = "pl.velivo.komunikator"
    compileSdk = 35
    defaultConfig {
        applicationId = "pl.velivo.komunikator"
        minSdk = 29
        targetSdk = 35
        versionCode = zmiany
        versionName = "1.22.$zmiany"
    }
    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    sourceSets["main"].java.srcDirs("src/main/java", "src/app/java")
    sourceSets["main"].res.srcDirs("src/main/res", "src/wspolne/res")   // 🖱 pomoc zdalna (wspólne)
    // 📹 strona rozmowy wideo - ta sama co w przeglądarce i na Windows
    sourceSets["main"].assets.srcDirs("../../src/Rozmowa")
}

// plik instalacyjny: Velivo-komunikator-release.apk
base { archivesName.set("Velivo-komunikator") }
