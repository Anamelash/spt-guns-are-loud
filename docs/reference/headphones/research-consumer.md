# Исследование реальных потребительских устройств

Дата проверки: 2026-09-07. Это справочник исходных данных для будущих расчётов мода. Значения относятся только к указанной модели/версии; NRR/SNR являются лабораторными рейтингами пассивного акустического пути и не являются измерением защиты от конкретного игрового выстрела. Для импульсного шума сами руководства Walker’s предупреждают, что NRR может быть неточным показателем.

## Идентификация в игре

| Игровое имя | Реальный кандидат | Уровень соответствия |
|---|---|---|
| Walker’s Razor Digital | Walker’s Razor Digital BT / Digital X-TRM — зависит от используемого идентификатора | provisional: у Walker’s есть несколько Razor Digital с разными рейтингами |
| Walker’s XCEL 500BT | Walker’s XCEL Digital GWP-XSEM-BT | высокий: точная модель и функции совпадают |
| Earmor M32 | EARMOR M32 / M32 Plus; M32 MOD3/MOD4 — отдельные ревизии | provisional: базовая модель в игровых данных не раскрывает ревизию |
| Cens proflex dx5 | CENS ProFlex DX5 | высокий по названию; реальные кастомные вкладыши, а не гарнитура |

Не смешивать Walker’s Razor Slim (аналоговые/обычные Slim и Slim Bluetooth) с Razor Digital X-TRM или старым Razor Digital BT. Также не смешивать EARMOR M32 с M32X: отдельная маркировка M32X требует отдельного документа и не может быть заполнена цифрами M32.

## Walker’s Razor Digital

### Razor Digital BT (GWP-DRQBT; источник — копия руководства)

Руководство указывает Bluetooth, четыре микрофона, складную дужку и 2×AAA. Таблица ANSI S3.19-1974: NRR 21 dB; среднее ослабление (дБ) на 125/250/500/1000/2000/3150/4000/6300/8000 Hz = **17.1 / 17.7 / 23.4 / 28.2 / 32.2 / 37.2 / 37.9 / 40.2 / 38.5**. SD приведены для 125–6300 Hz: **3.3 / 2.3 / 2.5 / 1.4 / 2.6 / 3.9 / 3.8 / 3.8**; для 8000 Hz в извлечённой таблице SD отсутствует. Время срабатывания, attack/release, компрессионная кривая, максимальный уровень усиления, вес, IP и рабочая температура в этом руководстве не указаны. Есть только предупреждение о правильной посадке и 2×AAA.

Источник: [Walker's Razor Digital BT User Manual (модельные обозначения GWP-DRQBT в документе)](https://manuals.plus/m/4bb12ba42f53510d01bc4c0b1b9c17185b6b4a8e99315e6a0eb70f248dd87582), строки таблицы и батареи — ANSI S3.19-1974. Это не хостинг Walker’s, поэтому уровень доказательности **B: опубликованная копия руководства**, пока не найден оригинальный PDF Walker’s.

### Razor X-TRM Digital (GWP-XDRSE) и Razor Digital X-TRM Bluetooth (GWP-XDRSEM-BT-GY)

Это две отдельные официальные карточки/SKU. GWP-XDRSE (`Razor X-TRM Digital`) указывает NRR **21 dB**, 2 hi-gain omnidirectional microphones, active dynamic sound suppression, 2×AAA и SAC **0.02 s**; Bluetooth на этой карточке не заявлен. GWP-XDRSEM-BT-GY (`Razor Digital X-TRM Bluetooth`) отдельно заявляет Bluetooth и 4-hour auto-off, а также те же NRR/SAC/microphone/battery claims. Точных band-by-band mean/SD, APV, attack/release, latency, gain cap, веса, IP и температуры на этих страницах нет.

Источники: [официальная карточка Razor Digital X-TRM](https://www.walkersgameear.com/razor-x-trm-digital-muffs/) и [страница Razor Digital Bluetooth XTRM](https://www.walkersgameear.com/razor-digital-bluetooth-xtrm/). Доказательность **A: производитель**.

### Что не использовать как Razor Digital

Официальный [Razor Slim Electronic](https://www.walkersgameear.com/razor-slim-electronic-ear-muff/) — NRR **23 dB**, 2 микрофона и SAC **0.02 s**; [Razor X-TRM](https://www.walkersgameear.com/razor-x-trm-muffs/) — NRR **21 dB**. Это разные продукты. Их цифры нельзя подставлять в игровой Razor Digital без подтверждения SKU.

## Walker’s XCEL 500BT (GWP-XSEM-BT)

Официальный оригинальный PDF Walker’s в открытом поиске не найден; доступна опубликованная копия руководства, привязанная к GWP-XSEM-BT. Она указывает: NRR **26 dB**, 2×AAA, Bluetooth, четыре режима (Universal, Speech Clarity, High Frequency, Power Boost), high-gain omnidirectional microphones, wind-noise reduction, voice prompts и programmable auto-off **2/4/6 h** (в одной копии default указан 2 h). Защита реализована как **variable dynamic sound suppression / sound-activated compression**, но численные attack/release, реакция, latency, threshold, gain и ceiling не раскрыты.

Источник: [копия User Guide XCEL Digital GWP-XSEM-BT](https://manuals.plus/m/f6717b14daeef6c64155d2a35f6b60c19b72da90290a7995a50a4450e485ffdd), [розничная карточка с точным SKU и 26 dB](https://www.tractorsupply.com/tsc/product/walkers-game-ear-xcel-500bt-digital-electronic-muff-with-voice-clarity-and-bluetooth-gsmgwpxsembt). Последняя также указывает массу упаковочного товара **1.08 lb** и габариты, но это не подтверждённая масса самих наушников — не использовать для расчёта. Доказательность чисел **B** (manual mirror/retailer), функции — **A/B**.

Не переносить на XCEL значения Razor Digital: XCEL имеет отдельный рейтинг 26 dB и другой DSP.

## EARMOR M32 / M32 Plus

### M32 Plus (официальное руководство, ревизия документа 2024-01)

Официальное руководство EARMOR M32 Plus содержит обе методики:

* ANSI S3.19-1974: NRR **22 dB**; mean на 125/250/500/1000/2000/3150/4000/6300/8000 Hz = **17.7 / 19.1 / 24.7 / 29.6 / 30.1 / 37.6 / 40.0 / 41.7 / 40.5**; SD = **3.1 / 1.9 / 2.3 / 2.3 / 2.2 / 3.2 / 3.4 / 2.7 / 3.1**.
* EN 352-1:2020: SNR **29 dB** (SNRm 30.3, SNRs 1.2); mean на 63/125/250/500/1000/2000/4000/8000 Hz = **20.5 / 16.4 / 21.5 / 25.4 / 29.7 / 30.2 / 41.6 / 41.3**; SD = **3.6 / 2.8 / 2.6 / 1.9 / 2.2 / 2.5 / 3.0 / 3.0**; mean−SD = **16.9 / 13.6 / 18.9 / 23.5 / 27.5 / 27.7 / 38.6 / 37.9**.

Другие численные данные: activation noise reduction level **82 dB**; микрофон −42±2 dB, omnidirectional; dynamic speaker 30 mm, 32 Ω, rated ≈30 mW, stated response **20 Hz–20 kHz**; 2×AAA; operating time **60 h**; operating temperature **−20…60 °C**, storage **−40…70 °C**; net weight **295±10 g**; 5 volume levels; auto-off **4 h**; audio input is limited to **82 dB** when connected to a personal music player. IP rating не заявлен. Сертификация: ANSI S3.19-1974 и EN 352-1/-4/-6/-8:2020. Руководство также отдельно упоминает до 200 h в описательном абзаце, но таблица спецификаций даёт 60 h; для расчёта использовать 60 h и считать 200 h противоречивой маркетинговой цифрой.

Источник: [официальный EARMOR M32 Plus User Manual](https://www.earmor.com/wp-content/uploads/2024/01/M32-Plus_EN_Usermanual_WebV1.pdf), pp. 1–5. Доказательность **A**.

### Другие M32 ревизии

При повторной проверке M32 Plus обнаружены расхождения самого руководства: на 8 кГц напечатано mean 41,3, SD 3,0 и mean−SD 37,9 дБ (арифметически получается 38,3). Исходные числа сохранены; точка APV требует проверки перед расчётом. Памятка хранения запрещает температуру выше 55 °C, тогда как таблица даёт −40…70 °C; условия не разъяснены. Дополнительно опубликованы предупреждение батареи при 2,55 В, отключение ниже 2,45 В, материалы ABS/POM/металл и разъём U174/u, 7 мм. Источник тот же M32 Plus manual, PDF-страницы 3, 5–6.

На странице с адресом EARMOR [M32 Mark4](https://www.earmor.com/product/m32-mark4/) отображаются NRR **22 dB**, SNR **28 dB**, Bluetooth 5.2, range 10 m, 2×AAA, up to 60 h, 40 mm speaker, 3.5 mm AUX, auto-off 4 h, −20…60 °C, storage −30…70 °C, ≈320 g, CE/RoHS/FCC и suppresses harmful noises above **80 dB**. Однако сама страница выглядит незавершённым CMS-шаблоном (цена $9,999, FAQ с текстом-заполнителем и описание M32 Plus). Эти числа сохраняются как **provisional/webpage-only**, в расчёты для M32 Plus не подставляются и не считаются подтверждёнными спецификациями Mark4 без отдельного руководства.

Официальная европейская карточка [M32 MOD3](https://www.earmor.eu/en/m32/) указывает NRR **22 dB**, 2×AAA, detachable mic, NATO TP-120, IPX-5, foldable; она не даёт полную band-table. **M32X не найден в подтверждённом официальном источнике; его характеристики остаются неизвестными.**

## CENS ProFlex DX5

Идентификация игрового предмета `Cens proflex dx5` соответствует реальному CENS ProFlex DX5. Это индивидуальные электронные вкладыши CENS/Puretone; passive module и electronic module — разные состояния, их таблицы нельзя смешивать.

Официальная брошюра DX Series сообщает SNR **25 dB** для DX5 и прямо говорит, что все пять режимов сохраняют один и тот же уровень защиты 25 dB. Режимы: **Game, Clay, Range, Wireless Comms, Hunter**, плюс mute; цифровой multi-switch, 10-ступенчатая громкость, mode/volume auto-save, wireless audio через supplied neck loop/optional SRC harness, Water-Shield, 2-year warranty. Game mode после выстрела снижает output и затем возвращает его; Clay рассчитан на более длительный частый огонь; Range снижает output для крупного калибра/закрытого тира; Hunter подавляет шаги по сухой растительности; Wireless Comms добавляет беспроводной вход/связь. Производитель не публикует величины порога, gain ceiling, attack/release или задержки возврата.

Официальное руководство ProFlex (UG510-EN-3.00) даёт EN352-2/EN352-7 таблицу для **electronic module**: частоты 63/125/250/500/1000/2000/4000/8000 Hz; mean attenuation = **26.1 / 24.6 / 23.2 / 23.3 / 23.4 / 31.6 / 31.8 / 36.4 dB**; SD = **6.4 / 4.6 / 3.8 / 3.5 / 3.4 / 3.6 / 3.3 / 4.8 dB**; assumed protection = **19.7 / 20.0 / 19.4 / 19.8 / 20.0 / 28.0 / 28.5 / 31.6 dB**; H=27, M=21, L=20, SNR=25. Для passive module таблица имеет mean **24.2 / 23.5 / 22.3 / 22.8 / 23.5 / 32.1 / 30.4 / 36.8**, SD **4.5 / 3.6 / 2.8 / 3.8 / 3.4 / 3.8 / 3.8 / 5.6**, assumed protection **19.7 / 19.9 / 19.5 / 19.0 / 20.1 / 28.3 / 26.6 / 31.2**, H=26, M=21, L=20, SNR=25.

Другие данные общего руководства семейства ProFlex: size-13 zinc-air battery, typical continuous use **up to 400 h** (это семейная цифра, не отдельный runtime DX5); DX3/DX5 instant start, DX1 имеет 0.5 s delay; loud sound автоматически уменьшает volume и после импульса возвращает его. IP-рейтинг не заявлен: Water-Shield — hydrophobic coating, не IP-класс. Вес, рабочая/складская температура, абсолютная latency, attack/release и output ceiling не опубликованы.

Источники: [официальная ProFlex DX Series brochure](https://www.censdigital.com/wp-content/uploads/2019/07/ProFlex-DX-Series-Download-Brochure.pdf), pp. 14–19; [официальное руководство CENS ProFlex UG510-EN-3.00](https://www.censdigital.com/wp-content/uploads/2019/07/CENSProFlexGuide-UG510-EN-3.00.pdf), pp. 3–5, 13, 17–19; [текущая карточка DX5](https://www.censdigital.com/proflex-dx5-hearing-protection/). Доказательность **A**.

## Поля, которые остаются неизвестными

Для всех четырёх игровых объектов производители не дают единого набора данных, который позволял бы честно вывести игровую передачу выстрела: отсутствуют waveform/transfer-function, точный impulse peak ceiling, release curve, microphone AGC/gain map, end-to-end latency (кроме CENS DX1 startup delay), внутриигровая посадка/утечка и индивидуальная вариативность. Не подменять эти поля NRR/SNR, рекламным “fast response” или временем SAC 0.02 s. При расчётах сохранять источник, модель, методику (ANSI или EN352), частоту, mean, SD/APV и пометку unknown для каждого незаявленного параметра.
