# ClipThat

Lekka aplikacja dla Windows do ciągłego nagrywania powtórek z gier i pulpitu w buforze RAM (1–10 min), z wielościeżkowym dźwiękiem WASAPI (system + mikrofon + gotowy miks), globalnym skrótem zapisu i wbudowanym edytorem przycinania klipów.

## Wymagania

- Windows 10 (2004 / build 19041 lub nowszy) albo Windows 11 (64-bit)
- Połączenie z internetem przy pierwszym uruchomieniu (automatyczna instalacja `.NET 8 SDK`, jeśli brakuje, oraz jednorazowe pobranie `ffmpeg.exe`)

## Instalacja i pierwsze uruchomienie

1. Na stronie repozytorium kliknij **Code → Download ZIP**.
2. *(Opcjonalnie)* Kliknij prawym przyciskiem pobrany plik `.zip` → **Właściwości** → zaznacz **Odblokuj** → **OK**.
3. Wypakuj archiwum do stałego folderu, np. `C:\ClipThat\`.
4. Kliknij dwukrotnie **`Start_ClipThat.bat`**.
   - Jeśli Windows wyświetli filtr SmartScreen, kliknij **Więcej informacji → Uruchom mimo to**.
   - Przy pierwszym uruchomieniu skrypt automatycznie doinstaluje `.NET 8 SDK` (jeśli nie ma go w systemie), pobierze `ffmpeg.exe`, skompiluje wersję `Release` i uruchomi **ClipThat**.
5. W oknie aplikacji wejdź w **Ustawienia** i włącz **„Uruchamiaj ClipThat po starcie systemu Windows (w ukrytym zasobniku)”** (opcja **„Rozpocznij nagrywanie od razu po uruchomieniu aplikacji”** jest domyślnie włączona) — od tej pory ClipThat będzie startować automatycznie w tle obok zegara (w zasobniku systemowym), bez potrzeby ponownego klikania skryptów.

## Aktualizacja do nowej wersji

Otwórz okno **ClipThat** i kliknij przycisk **Aktualizacja** (ikona strzałki w dół) na samym dole lewego paska nawigacji:

- Aplikacja sprawdzi na GitHubie, czy pojawiły się nowe zmiany. Jeśli masz już najnowszą wersję, poinformuje Cię o tym komunikatem.
- Jeśli dostępna jest aktualizacja i ją potwierdzisz, ClipThat automatycznie się zamknie, pobierze najnowsze pliki (zachowując pobrany wcześniej `Assets\ffmpeg.exe`), przekompiluje wersję `Release` i uruchomi się ponownie.
- **Twoje ustawienia i nagrania pozostają nietknięte** — konfiguracja jest zapisana osobno w `%AppData%\ClipThat\settings.json`.
