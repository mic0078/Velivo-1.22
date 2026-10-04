# Testy bezpieczeństwa haseł i formularzy

```
python3 testy/bezpieczenstwo/wyciagnij.py
NODE_PATH=$(npm root -g) node testy/bezpieczenstwo/ataki.js   # 17 ataków złośliwej strony w Chromium
python3 testy/bezpieczenstwo/domeny.py                         # 12 stron-podróbek
```
Kod wyjścia 0 = wszystkie ataki odparte.
