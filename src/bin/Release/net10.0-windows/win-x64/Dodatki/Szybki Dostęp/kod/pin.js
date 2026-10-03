'use strict';

// =====================================================================
//  Okienko PIN-u przy dodawaniu strony do cudzego profilu
// =====================================================================
//  To okno NIE zna zadnych danych profilu i nie ma do nich dostepu.
//  Odsyla tylko wpisany kod do procesu tla, a ten sam sprawdza skrot
//  i sam dodaje skrot. Dzieki temu nie da sie tego okna obejsc -
//  zamkniecie go po prostu niczego nie doda.

const $ = (id) => document.getElementById(id);

function zamknij() {
  chrome.windows.getCurrent().then((w) => chrome.windows.remove(w.id)).catch(() => window.close());
}

async function start() {
  try {
    const o = await chrome.runtime.sendMessage({ typ: 'oczekujaceDodanie' });
    if (!o || !o.ok) {
      $('opis').textContent = 'Nie ma na co czekac - zgloszenie wygaslo.';
      $('pole').disabled = true;
      $('ok').disabled = true;
      return;
    }
    $('tytul').textContent = 'Profil "' + o.profil + '" jest na PIN';
    $('opis').textContent = 'Podaj kod, zeby dodac te strone do profilu "' + o.profil + '".';
  } catch (e) {
    $('opis').textContent = 'Proces tla nie odpowiada. Zamknij to okno i sprobuj ponownie.';
  }
  $('pole').focus();
}

async function potwierdz() {
  const pin = $('pole').value;
  $('blad').textContent = '';
  $('ok').disabled = true;
  try {
    const w = await chrome.runtime.sendMessage({ typ: 'dodajZPinem', pin });
    if (w && w.ok) { zamknij(); return; }
    $('blad').textContent = (w && w.powod) ? w.powod : 'Nie udalo sie dodac.';
  } catch (e) {
    $('blad').textContent = 'Proces tla nie odpowiada.';
  }
  $('ok').disabled = false;
  $('pole').select();
  $('pole').focus();
}

$('ok').addEventListener('click', potwierdz);
$('anuluj').addEventListener('click', zamknij);
$('pole').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { e.preventDefault(); potwierdz(); }
  if (e.key === 'Escape') { e.preventDefault(); zamknij(); }
});

start();
