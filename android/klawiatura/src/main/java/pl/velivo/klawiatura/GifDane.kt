package pl.velivo.klawiatura

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.File
import java.io.FileNotFoundException

// 🎞 GIF z telefonu wysłany z klawiatury: kopia w pamięci podręcznej klawiatury, udostępniona tylko aplikacji, do której piszesz (jednorazowe prawo odczytu)
class GifDane : ContentProvider() {
    override fun onCreate() = true
    private fun file(u: Uri): File? {
        val n = u.lastPathSegment ?: return null
        if (!n.matches(Regex("velivo-\\d+\\.gif"))) return null   // tylko kopie GIF-ów z klawiatury, nic innego
        return File(File(context!!.cacheDir, "gif"), n).takeIf { it.isFile }
    }
    override fun openFile(u: Uri, mode: String): ParcelFileDescriptor = ParcelFileDescriptor.open(file(u) ?: throw FileNotFoundException(), ParcelFileDescriptor.MODE_READ_ONLY)
    override fun getType(u: Uri) = "image/gif"
    override fun query(u: Uri, p: Array<out String>?, s: String?, a: Array<out String>?, o: String?): Cursor? {
        val f = file(u) ?: return null
        return MatrixCursor(arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)).apply { addRow(arrayOf<Any>(f.name, f.length())) }
    }
    override fun insert(u: Uri, v: ContentValues?): Uri? = null
    override fun delete(u: Uri, s: String?, a: Array<out String>?) = 0
    override fun update(u: Uri, v: ContentValues?, s: String?, a: Array<out String>?) = 0

    companion object { const val AUTH = "pl.velivo.klawiatura.gif" }
}
