plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "pl.velivo.browser"
    compileSdk = 35
    defaultConfig {
        applicationId = "pl.velivo.browser"
        minSdk = 29
        targetSdk = 35
        versionCode = 2
        versionName = "1.22"
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
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    // Szybki Dostęp: ta sama strona co w Velivo na Windows - jedno źródło dla obu wersji
    sourceSets["main"].assets.srcDirs("src/main/assets", "../../src/QuickAccessExtension/kod")
}

// plik instalacyjny: Velivo-release.apk (zamiast app-release.apk)
base { archivesName.set("Velivo") }

dependencies {
    implementation("androidx.webkit:webkit:1.12.1")
    // odtwarzacz filmów i telewizji (IPTV): Media3 / ExoPlayer - lekki, sprzętowe dekodowanie, HLS i MPEG-TS
    implementation("androidx.media3:media3-exoplayer:1.5.0")
    implementation("androidx.media3:media3-exoplayer-hls:1.5.0")
    implementation("androidx.media3:media3-ui:1.5.0")
    implementation("org.jellyfin.media3:media3-ffmpeg-decoder:1.5.0+1")   // dźwięk AC3/E-AC3/MP2 z kanałów TV - telefony często nie mają tych dekoderów
}
