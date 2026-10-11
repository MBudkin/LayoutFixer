# Архитектура LayoutFixer

Подробное техническое описание. Для короткой выжимки и инвариантов см.
`AGENTS.md` в корне.

---

## 1. Обзор

LayoutFixer — Windows-трей утиита, которая:

1. Перехватывает клавиатурный ввод через глобальный low-level hook.
2. Накапливает «видимое» слово в буфере.
3. В двух точках решает, не набрано ли слово не в той раскладке:
   - **Live** — во время ввода слова (на префиксах).
   - **Final** — после разделителя (по полному слову).
4. При положительном решении заменяет хвост текста одним атомарным
   `SendInput` и переключает системную раскладку через
   `WM_INPUTLANGCHANGEREQUEST`.
5. Поддерживает пользовательский словарь (правильные слова в раскладке),
   пользовательские замены (триггер→фраза), горячие клавиши и ручной
   откат (Pause).
6. Может автоматически ставить заглавную букву после завершения предложения,
   исправлять характерную ошибку включённого Caps Lock, менять регистр текста
   и транслитерировать в обе стороны RU ↔ Latin.

Раскладки — только **RU/EN**, по первичному языку (`LayoutProbe.LanguageOf`):
любые английские (US, UK, …) и русские варианты. При активной третьей
раскладке (украинская и т. п.) автоматические исправления не выполняются.

---

## 2. Поток данных при нажатии клавиши

```
Физическая клавиша
   │
   ▼
KeyboardHook.HookCallback   [WH_KEYBOARD_LL, на UI-потоке]
   │  отсеивает injected-события (свои же SendInput)
   │  создаёт KeyboardObservedEventArgs(VK, scanCode)
   ▼
TrayApplicationContext.HandleKeyObserved
   │
   ├─ если _converting (идёт ручная команда): пометить «буфер устарел», return
   ├─ если это зарегистрированный hotkey: return (команду примет WM_HOTKEY)
   ├─ поле пароля (InputContextGuard): Clear + ClearUndo, клавиша не пишется
   ├─ Tab без "TabAsWordSeparator": Clear + ClearUndo
   │
   ├─ isTerminator = IsWordTerminator(VK)   Space/Enter/Tab/OemMinus
   ├─ reset = _keyboardBuffer.Observe(args)
   │     ├─ Backspace/Delete → RemoveLast (reset=false) + NotifyTrackedDeletion
   │     ├─ Ctrl+Backspace/Ctrl+Delete → Clear (reset=true)
   │     ├─ Navigation (стрелки/Home/...) → Clear (reset=true)
   │     ├─ чистые модификаторы → no-op
   │     ├─ Ctrl/Alt+key → Clear (reset=true)
   │     └─ typable → TranslateKey → Append (reset=false)
   │
   ├─ если reset: _conversionService.ClearUndo()
   │
   ├─ если isTerminator: HandleTerminatorInHook()   [СИНХРОННО, в хуке]
   │     ├─ AutoEditsAllowed()? (пароль / исключение / полный экран)
   │     ├─ using InputBatch(pendingTail = разделитель):
   │     │     ApplyReplacement → (AutoMode) FinalConvert
   │     │     → FixAccidentalCapsLock → FixTwoInitialCapitals
   │     │     → AutoCapitalizeLastWord
   │     └─ batch.HasChanges → Commit (один SendInput) + args.Suppress=true
   │
   └─ иначе если AutoMode, IsLiveEligibleKeystroke(VK), AutoEditsAllowed():
         └─ TryLiveConvert()
               ├─ HIT → PerformLiveReplace + args.Suppress=true
               └─ args.Suppress=true → HookCallback возвращает 1 (клавиша подавлена)
```

### Почему разделитель обрабатывается в хуке

Низкоуровневый хук вызывается ДО доставки клавиши приложению. Буфер уже
содержит разделитель, приложение — ещё нет. `InputBatch` сводит все правки шагов
конвейера к одной замене относительно экрана (`Backspaces` + `Text`, где `Text`
заканчивается самим разделителем). Если правок нет, клавиша проходит как есть;
если есть — она подавляется, а замена с разделителем уходит одним `SendInput`
(`\n` → `VK_RETURN`, `\t` → `VK_TAB`). Поэтому в мессенджере по `Enter`
уходит уже исправленное слово, а клавиши, нажатые сразу после разделителя, не
могут попасть внутрь замены.

### Сторож хуков

Windows молча снимает низкоуровневый хук, если его колбэк однажды не уложился в
`LowLevelHooksTimeout`. Каждые 2 с `CheckHooksAlive` сравнивает
`GetLastInputInfo` с временем последнего вызова каждого хука: если система
получила ввод, которого не видел ни один хук (и на переднем плане не окно с
правами администратора / защищённый рабочий стол), хуки переустанавливаются.
Так же — после разблокировки сеанса и выхода из сна.

---

## 3. Ключевые классы (в `src/LayoutFixer/Input/`)

### `KeyboardHook`
Обёртка над `SetWindowsHookEx(WH_KEYBOARD_LL)`. На каждом не-injected
WM_KEYDOWN/WM_SYSKEYDOWN создаёт `KeyboardObservedEventArgs`. Если
обработчик выставил `args.Suppress`, callback возвращает `1` — клавиша не
доходит до приложения. Это основа live-замены.

### `KeyboardBuffer`
Накапливает переведённые символы (через `ScanCodeMap` + `ToUnicodeEx`
fallback) в `StringBuilder` (макс. 128). Разделители: `" \t\r\n!()-"`.
`TryGetLastToken` возвращает `BufferToken(Text, Trailing)` — слово без
краевых разделителей и сами разделители отдельно. `ReplaceTail(count, text)`
используется для синхронизации буфера после конвертации.

### `LayoutConverter`
Две хэш-таблицы EN↔RU (`EnglishToRussian`, обратная построена реверсом).
`Convert(text, sourceLanguage)` переводит посимвольно; `DetectLanguage`
считает попадания в каждый алфавит (только буквы; ничья → English).

### `LayoutDecisionEngine` — решения без побочных эффектов
Чистая логика: `DecideLive(token, systemLanguage)` и
`TryFinalDecision(text, previousWord, out target)`. Не трогает буфер, не
отправляет ввод, не переключает раскладку — поэтому покрыта тестами
(`tests/LayoutFixer.Tests`) и прогоняется инструментом оценки
(`tools/LayoutFixer.Tools`). Пороги — в `NgramDecisionOptions`.

### `NgramModel`, `ShortWordRanks`
- `NgramModel` — триграммная модель букв одного языка (`^слово$`, сглаживание
  Witten-Bell). `Score(word, complete)` возвращает среднюю log-вероятность на
  переход, число невиданных триграмм и `Relative` — оценку относительно
  типичного слова той же длины (строки `#baseline` в таблице).
- `ShortWordRanks` — ранги частотности слов из 1–4 букв.

### `ConversionService` — состояние слова и замены
Поля состояния:
- `_lastConversion` — последняя автоконвертация (для toggle/revert).
- `_lastCapitalization` — последняя автоматически поставленная заглавная
  буква. Это отдельный одношаговый слой отмены над `_lastConversion`.
- `_currentWordHadLiveTrigger` — в слове уже был live (final пропускается до
  разделителя); обычный Backspace/Delete снимает флаг, чтобы исправленное
  продолжение слова снова анализировалось на следующей введённой букве.
- `_revertedWord` — слово, выбранное пользователем вручную: отменённое
  Pause'ом или полученное ручной конвертацией Break (автоконвертация его не
  трогает).
- `_previousWordLock` — то же для слова перед текущим: однобуквенное слово,
  выбранное вручную, не конвертируется вместе со следующим словом.

Методы:
- `TryLiveConvert()` — см. ниже.
- `FinalConvert()` — финальное решение после разделителя.
- `TryFinalDecision()` — выбор раскладки-цели для финала.
- `PerformLiveReplace()` — общая механика live-замены.
- `ConvertLastWord()` — ручная конвертация (Pause), с toggle/revert.
- `FixAccidentalCapsLock()` — при реально включённом Caps Lock нормализует
  `пРИВЕТ` в `Привет` и выключает Caps Lock.
- `AutoCapitalizeLastWord()` — заглавная буква после `.`, `!`, `?` и пробела.
- `TransformLastWord()` / `TransformSelection()` — регистр и двусторонний
  транслит. Регистр либо переключается целиком, либо инвертируется у каждой
  буквы согласно настройке; смешанная кириллица+латиница не транслитерируется.
- `ApplyReplacement()` — пользовательские замены.
- `Revert()` — откат последней конвертации.
- `ConvertSelection()` — конвертация выделения (Shift+Pause).

### `TriggerDictionaries`
Загружает встроенные таблицы (`LiveTriggers_{ru,en}`, `FinalTriggers_{ru,en}`
и сгенерированную `final-words-en`, которая дополняет `FinalTriggers_en`) в
`HashSet`. При загрузке отбрасывает `SystemAbbreviations` (`ctrl`, `alt`,
`f1..f12` и т.д.). Записи хранятся в нижнем регистре **без** свёртки `ё`→`е`;
запрос с `ё` дополнительно ищется с `е`, обратное запрещено (инвариант 23).

### `AffixModels`
Загружает 4 аффиксные таблицы (RU/EN × префиксы/суффиксы). `Score(word)`
возвращает вес: каждый matched аффикс добавляет свою длину. **Одинокий
3-буквенный суффикс без префикса игнорируется** (защита от `ger` на `ueger`).

### `LayoutSwitcher`
- `SwitchTo(language)` — полная: находит/загружает раскладку, PostMessage.
- `PostSwitchTo(language)` — тонкая обёртка для live (без `Sleep`/foreground).

### `TextInputSender`, `InputBatch`
- `ReplaceTail(backspaceCount, replacement)` — N backspace + текст, **одним**
  вызовом `SendInput`; внутри открытого `InputBatch` только копит правку.
- `SendString(text)` — текст без backspace (для выделения).
- Текст уходит Unicode-событиями (раскладко-независимо); `\r\n`/`\n` →
  реальный VK_RETURN, `\t` → VK_TAB; у VK-событий проставлен скан-код.
- `InputBatch` — пакет для разделителя, см. раздел 2.

### `InputContextGuard`
WinEvent-хуки (смена активного окна и фокуса, вне контекста) + фоновый поток,
который проверяет, не поле ли пароля в фокусе (Win32 `ES_PASSWORD`, затем
UI Automation `IsPassword`). Клик мышью, `Tab` и `Enter` тоже запускают
проверку. Хук читает только кэш: `IsPasswordFieldFocused`, `IsProbePending`,
`GetAutoBlockReason(excludedApps, disableInFullscreen)`.

### Чтение выделения
Все команды выделения используют `SelectionProbe.TryReadSelectedText`:
1. `TextPattern.GetSelection()` у сфокусированного UI Automation-элемента.
2. Та же проверка у его родителей (до 12 уровней).
3. При подтверждённом жесте выделения — fallback `WM_COPY`, затем `Ctrl+Insert`.

`_selectionIntent` ставится после протягивания/двойного щелчка мышью,
`Shift+стрелки` или `Ctrl+A` и снимается обычным кликом, вводом и навигацией.
Служебный UIA-диапазон только из пробелов/переводов строк игнорируется.
`Ctrl+C` и `Ctrl+Shift+C` не используются, потому что некоторые редакторы
копируют строку, ссылку или путь даже при пустом выделении.
Перед clipboard-fallback `ClipboardSnapshot` сохраняет все форматы буфера
обмена, основанные на памяти (текст, HTML, RTF, DIB/PNG, списки файлов), и
затем восстанавливает их; владельцем буфера при восстановлении выступает
скрытое окно программы.

---

## 4. Логика live-фазы (`TryLiveConvert` → `DecideLive`)

Вызывается на каждой типабельной не-терминаторной клавише. Возвращает `true`
если выполнила замену (тогда вызывающий ставит `args.Suppress=true`).

1. Фильтры: `_currentWordHadLiveTrigger`, токен без букв, токен с цифрами,
   раскладка не RU/EN → `false`. Пароль/исключение/полный экран отсекаются
   раньше, в `AutoEditsAllowed`.
2. `IsRevertedWord(token.Text)` → `false` (не трогать отменённое слово).
3. Иначе блокировка переходит в `_previousWordLock` (слово изменилось).
4. **Oscillation / edge guard** (инвариант 6).
5. **Hit**: converted-форма — LiveTrigger целевого языка; иначе n-граммы:
   видимое начало (≥ 4 букв, только буквы) содержит ≥ 2 невиданных триграмм,
   converted-начало — без невиданных и не хуже типичного, разница ≥ `LiveMargin`.
6. Видимая форма сама LiveTrigger своего языка (`ищу` ↔ `boe`) → нет переключения.
7. **Hold**: видимое начало (выровненное, инвариант 26) — частое слово текущего
   языка (`LiveHoldRank`) или, для trigger-hit, читается как нормальное начало
   (`LiveVetoMargin`) → ждать final.
8. **User guards**: UserDictionary в текущей раскладке / UserReplacement
   (только visible) → ждать; UserDictionary в целевой раскладке согласован.
9. `PerformLiveReplace`: N−1 backspace, полный converted в буфер,
   `PostSwitchTo`, `_currentWordHadLiveTrigger = true`, запись `_lastConversion`.
   Однобуквенное слово перед токеном (`z lev` → `я дум`) заменяется тем же
   пакетом (инвариант 37).

> Подробности и инварианты — в `AGENTS.md`, раздел «Критические инварианты».

---

## 5. Логика final-фазы (`FinalConvert` → `TryFinalDecision`)

Вызывается из `HandleTerminatorInHook` после `ApplyReplacement` (если замена не
сработала). Если после слова уже был разделитель (`HasEarlierTerminator`),
весь конвейер пропускается (инвариант 35). Порядок:

1. `_currentWordHadLiveTrigger` → skip (слово уже было обработано live).
2. Раскладка не RU/EN (третья раскладка) → skip.
3. Фильтры (нет токена, цифры, нет букв), `IsRevertedWord` → skip.
4. `TryFinalDecision` (при включённом Caps Lock — для слова в задуманном
   регистре, `dSGBIB` → `Dsgbib`, инвариант 36):
   - **UserDictionary** (приоритет): visible в текущей → оставить; converted в
     другой → заменить+переключить; обе → skip. Для converted со
     сгенерированным ведущим мусором (`"было"` → `@,skj@`) trimmed-lookup
     запрещён.
   - **Адреса** (`x@y`, `://`, `www.`) и converted = имя клавиши → оставить.
   - **Идентификаторы** (строчная→заглавная внутри слова в обеих формах:
     `VkShift`, `iPhone`) → оставить. Live-путь проверяет то же самое.
     Кириллическое слово со смешанным регистром (`СрфеПЗЕ`) решается по
     строчной форме (инвариант 39).
   - **Короткие слова** (2–4 буквы, не ВСЕ ЗАГЛАВНЫЕ): ранги видимой
     (выровненной) и converted формы; converted должна быть частотнее в
     `exp(ShortMargin)` раз (≈30). Предыдущее слово той же раскладки повышает
     порог, другой — понижает; ВСЕ ЗАГЛАВНЫЕ (аббревиатура) — повышают ещё.
     Видимая — известное слово и порог не взят → оставить. Короткий токен
     ВСЕ-ЗАГЛАВНЫМИ без частотного решения дальше не идёт (`DIB` ≠ `ВШИ`).
   - **Точные триггеры**: FinalTriggers (+ final-words-en) и LiveTriggers →
     односторонний hit решает, двусторонний для ≤3 букв → RU, иначе skip.
   - **N-граммы**: converted нормальна (`Relative ≥ FinalMinTarget`) и лучше
     видимой на `FinalMargin` → заменить; видимая нормальна, все триграммы
     известны и converted не лучше → оставить.
   - **Аффиксы** (слово ≥ 4 букв): только если n-граммы не против
     (`AffixMinAdvantage`).
   - **Контекст**: слово из 1–2 букв после слова той же раскладки не меняется
     (слово с `.`/`?`/`…` на конце, `!` или перевод строки после него
     контекста не дают). Исключение — русская буква среди английских
     терминов (`Claude b ChatGPT`) или, при настройке
     `AlwaysConvertSingleLetters` (по умолчанию), после любого английского
     слова: исправляется без переключения раскладки (инвариант 38).
   - Edge-правила инварианта 11 (trimmed-lookup с изменённой краевой
     пунктуацией запрещён, парные обёртки разрешены) действуют для всех
     слоёв, кроме UserDictionary.
5. Переключение раскладки (`PostSwitchTo` внутри пакета), `ReplaceTail`,
   запись `_lastConversion`. Однобуквенное слово перед токеном, оставленное
   контекстом (`hello z levf`), заменяется вместе с ним (инвариант 37).

---

## 6. Пользовательские данные

Все файлы пишутся через `AtomicFile` (временный файл + `File.Replace`,
резервная копия `*.bak`; нечитаемый файл откладывается как `*.corrupt-<дата>`
и данные берутся из резервной копии). Папку данных можно переопределить
переменной окружения `LAYOUTFIXER_DATA_DIR` (`AppPaths`).

### UserDictionary (`%AppData%\LayoutFixer\user-dictionary.json`)
Две корзины: `Russian`, `English`. Слова — правильные слова в данной
раскладке. JSON:
```json
{ "russian": ["гупук", "хошь"], "english": ["ueger"] }
```
- `ContainsRussian/English` — точное совпадение (нормализованное).
- `HasRussianPrefix/HasEnglishPrefix` — префиксное (для live-guards).
- `Replace` (правка/перенос в другой список), `RemoveRange` — одним сохранением.
- Событие `Changed` после каждого сохранения (может прийти с любого потока;
  окна маршалят его к себе).
- Финальная роль: приоритетный словарь раскладки.

### UserReplacements (`%AppData%\LayoutFixer\user-replacements.json`)
Список `{trigger, replacement, caseSensitive, enabled}`. Exact-visible
замена: проверяет только итоговое видимое слово, никогда — converted.
- `Find(token, respectPunctuation, out prefix, out suffix)` — для final.
- `HasTriggerPrefix(visible)` — для live-guard.
- `SetAll` сохраняет только полные пары; событие `Changed` — как у словаря.
  Окно замен сохраняет правки с задержкой ~0,7 с, поэтому пара работает,
  пока окно ещё открыто.

> КРИТИЧНО: UserDictionary проверяет обе формы (visible + converted);
> UserReplacement — только visible. Цепочка `gynh→пнтр→фраза` запрещена.

---

## 7. Настройки

`AppSettings` (`%AppData%\LayoutFixer\settings.json`). Ключевые поля для
автоконвертации:
- `AutoMode` (bool, default true) — мастер-тумблер live + final.
- `AutoCapitalizeSentences` (bool, default false) — заглавная буква в начале
  нового предложения; работает независимо от `AutoMode`.
- `FixAccidentalCapsLock` (bool, default true) — исправляет `пРИВЕТ` в
  `Привет`, только когда Caps Lock действительно включён, затем выключает его.
- `FixTwoInitialCapitals` (bool, default true) — `ПРивет` → `Привет`
  (≥ 4 букв одного алфавита, две заглавные, дальше только строчные). Слой
  регистра: первый Break возвращает набранное.
- `DisableInFullscreenApps` (bool, default true) — не делать автоматических
  правок в полноэкранном окне без рамки.
- `ExcludedApps` (список имён `*.exe`) — программы без автоматических правок.
- `InvertEachCharacterCase` (bool, default false) — горячая клавиша регистра
  инвертирует каждую букву отдельно (`Привет` → `пРИВЕТ`) вместо переключения
  всего текста между верхним и нижним регистрами.
- `AutoReplace` (bool, default true) — вкл/выкл пользовательские замены.
- `TabAsWordSeparator` (bool, default true).
- `ReplacementsRespectPunctuation` (bool, default true).
- `ClearBufferAfterConversion`, `PlaySoundOnConversion`, `SoundVariant`,
  `SoundVolume`, `CustomSoundPath`, `ManualLearnMode` (default `Ask`),
  `SelectionLearnMode` (default `Never`),
  `TrayIconMode` (`Language`/`Flag`/`Program`, default `Language`). При
  выключенном `AutoMode` любой из трёх вариантов отображается в оттенках серого;
  цвет возвращается сразу после включения автопереключения.

Если `AutoMode == false`, ручная конвертация последнего слова (`Pause`/`Break`)
остаётся доступной, но не обучает пользовательский словарь и не показывает
prompt «запомнить».

- `CheckForUpdates` (bool, default true), `LastUpdateCheckUtc`,
  `SkippedUpdateVersion` — проверка обновлений (см. раздел 7.1).

Восстановление резервной копии копирует настройки через
`AppSettings.CopyFrom` (кроме служебных полей проверки обновлений). Тест
`BackupRestoreCopiesEverySetting` падает, если новое свойство забыли там
перечислить.

UI настроек — `UI/SettingsForm.cs`: навигация слева (`SettingsPage`:
Основные, Исправление, Словари и замены, Исключения, Горячие клавиши, Звук,
Дополнительно) и по странице справа. Тексты контролов регистрируются один раз
(`Register(control, textKey, tooltipKey)`) и применяются в `ApplyLocalization`.
Изменения из меню трея и горячих клавиш попадают в открытое окно через
`SettingsForm.RefreshFromSettings` (`TrayApplicationContext.SyncOpenWindows`).
Hotkey-и регистрируются через
`HotkeyManager` (RegisterHotKey; пять действий: ConvertLastWord,
ConvertSelection, ToggleEnabled, ToggleCase, Transliterate).
Перед ручной отправкой текста обработчик ждёт отпускания Ctrl/Alt/Shift/Win,
чтобы инжектированный Backspace не превратился в команду редактора.

### 7.1. Проверка обновлений

`Services/UpdateChecker` — один GET к
`https://api.github.com/repos/MBudkin/LayoutFixer/releases/latest`
(заголовок `User-Agent: LayoutFixer/<версия>`, таймаут 20 с). Версия берётся
из `tag_name` (или `name`), черновики и pre-release игнорируются, ссылка на
страницу выпуска принимается только внутри `github.com/MBudkin/LayoutFixer`.
`UI/UpdateCoordinator` живёт на UI-потоке: первая проверка через минуту после
запуска, затем раз в час смотрит, прошли ли сутки с `LastUpdateCheckUtc`.
Найденная (и не пропущенная) версия показывается неактивирующим
`TrayPrompt` и пунктом меню трея; `UpdateForm` открывает страницу выпуска в
браузере. Программа ничего не скачивает и не запускает.

---

## 8. Встраиваемые ресурсы (`LayoutFixer.csproj`)

```
Assets\Logo1.png                                   → Logo1.png
Resources\Dictionaries\{ru,en}-{prefixes,suffixes}.txt → Dictionaries.*
Resources\Dictionaries\{Live,Final}Triggers_{ru,en}.txt → Dictionaries.live/final-triggers-*
Resources\Dictionaries\ngrams-{ru,en}.txt         → Dictionaries.ngrams-*        (генерируется)
Resources\Dictionaries\short-words-{ru,en}.txt    → Dictionaries.short-words-*   (генерируется)
Resources\Dictionaries\final-words-en.txt         → Dictionaries.final-words-en  (генерируется)
..\..\layout-switch-sound{1,2}.wav                → Sounds.sound{1,2}.wav
```

Генерируемые таблицы создаёт `tools/LayoutFixer.Tools ngrams`, см.
`tools/LayoutFixer.Tools/README.md`.

`Resources/Strings.{ru,en}.resx` — локализация (нейтральный `Strings.resx` —
русский для первого релиза).

---

## 9. Что НЕ делает программа

- Не работает с раскладками кроме RU/EN (и не трогает текст, набранный в них).
- Не хранит историю ввода (конфиденциальность); `debug.log` по умолчанию
  выключен и ограничен по размеру.
- Не имеет телеметрии. Единственное сетевое обращение — необязательная
  проверка обновлений (номер последней версии на GitHub), отключается в
  настройках.
- Не видит ввод в окнах с правами администратора, если сама запущена без них
  (ограничение Windows для низкоуровневых хуков).
