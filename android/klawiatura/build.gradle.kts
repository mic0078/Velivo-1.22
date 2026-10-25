plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Klawiatura Velivo - osobna aplikacja (ten sam klucz podpisu co Velivo); numer wersji jak w Velivo
val zmiany = runCatching { providers.exec { commandLine("git", "rev-list", "--count", "HEAD") }.standardOutput.asText.get().trim().toInt() }.getOrDefault(2)

android {
    namespace = "pl.velivo.klawiatura"
    compileSdk = 35
    defaultConfig {
        applicationId = "pl.velivo.klawiatura"
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
    // 📹 strona rozmowy wideo - ta sama co w przeglądarce i na Windows
    sourceSets["main"].assets.srcDirs("src/main/assets", "../../src/Rozmowa")
    // 💬 komunikator - jedno wspólne źródło dla przeglądarki i klawiatury (android/komunikator)
    sourceSets["main"].java.srcDirs("src/main/java", "../komunikator/src/main/java")
}

// plik instalacyjny: Velivo-klawiatura-release.apk
base { archivesName.set("Velivo-klawiatura") }
