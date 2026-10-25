plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// numer wersji rośnie sam z każdą zmianą (liczba zmian w git) - nowy plik instaluje się na starym jako aktualizacja,
// z zachowaniem danych (ten sam klucz podpisu); tak samo w Google Play
val zmiany = runCatching { providers.exec { commandLine("git", "rev-list", "--count", "HEAD") }.standardOutput.asText.get().trim().toInt() }.getOrDefault(2)

android {
    namespace = "pl.velivo.browser"
    compileSdk = 35
    defaultConfig {
        applicationId = "pl.velivo.browser"
        minSdk = 29
        targetSdk = 35
        versionCode = zmiany
        versionName = "1.22.$zmiany"
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a") }   // tylko telefony ARM - mniejszy plik (VLC ma kod natywny)
    }
    // Trzy wersje z jednego kodu:
    //  pelna  - APK dla rodziny (YouTube bez reklam i w tle, dokładny budzik) - jak dotąd, ten sam identyfikator;
    //  sklep  - Google Play, ostrożna: bez skryptu YouTube i bez YouTube w tle; budzik za zgodą użytkownika;
    //  sklepyt - Google Play jak Brave: blokada reklam także na YouTube i granie w tle, ale bez reklamowania tego (napisy jak w "sklep").
    flavorDimensions += "wersja"
    productFlavors {
        create("pelna") {
            dimension = "wersja"
            applicationId = "pl.velivo.browser"
            buildConfigField("boolean", "PELNA", "true")
            buildConfigField("boolean", "YOUTUBE", "true")
        }
        create("sklep") {
            dimension = "wersja"
            applicationId = "pl.velivo"
            buildConfigField("boolean", "PELNA", "false")
            buildConfigField("boolean", "YOUTUBE", "false")
        }
        create("sklepyt") {
            dimension = "wersja"
            applicationId = "pl.velivo"   // ten sam co "sklep" - do sklepu wysyłasz jedną z nich
            buildConfigField("boolean", "PELNA", "false")
            buildConfigField("boolean", "YOUTUBE", "true")
        }
    }
    buildFeatures { buildConfig = true }
    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    // Szybki Dostęp: ta sama strona co w Velivo na Windows - jedno źródło dla obu wersji
    sourceSets["main"].assets.srcDirs("src/main/assets", "../../src/QuickAccessExtension/kod", "../../src/Rozmowa")
    // 💬 komunikator - jedno wspólne źródło dla przeglądarki i klawiatury (android/komunikator)
    sourceSets["main"].java.srcDirs("src/main/java", "../komunikator/src/main/java")
    sourceSets["main"].res.srcDirs("src/main/res", "../komunikator/src/wspolne/res")   // 🖱 pomoc zdalna (wspólne)
}

// plik instalacyjny: Velivo-release.apk (zamiast app-release.apk)
base { archivesName.set("Velivo") }

dependencies {
    implementation("androidx.webkit:webkit:1.12.1")
    // odtwarzacz filmów i telewizji: biblioteka VLC (jak VLC na Windows) - każdy format: stare AVI, AC3/E-AC3, MPEG-TS, HLS
    implementation("org.videolan.android:libvlc-all:3.6.5")
}
