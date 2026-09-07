# 3M PELTOR: справочник для аудио-моделирования

Дата проверки: 2026-09-07. Область: ComTac II, Tactical Sport/SportTac, ComTac IV Hybrid, ComTac V, ComTac VI и TEP-300. Числа ниже относятся только к указанным артикулам, подушкам и вкладышам. `MV`/mean — среднее лабораторное затухание; `SD` — стандартное отклонение; `APV = MV − SD` — заявленная предполагаемая защита. Это не АЧХ игрового файла, не коэффициент Unity и не dBFS.

## Что можно использовать в расчётах

* Таблицы затухания — это измерение пассивного защитного тракта по стандарту, а не частотная характеристика усилителя окружающего звука. Их нельзя напрямую превращать в EQ кривую без модели ушного/измерительного тракта.
* SNR/H/M/L и NRR — маркировка защиты с конкретной методикой. Не переводить NRR в EQ и не складывать SNR с внутриигровыми децибелами.
* `Criterion level` у SportTac — уровень входа, при котором электронная схема достигает критерия; это не ceiling выхода и не громкость в dBFS.
* Для ComTac V/VI встречаются разные региональные версии, подушки, ремни, кабели и NIB. Перенос таблицы между ними допустим только как явно помеченная оценка семейства.
* 3M предупреждает, что реальная защита ниже этикетки зависит от посадки, навыка и мотивации пользователя; 3M рекомендует fit-test.

## ComTac II (MT15H69)

Архивная инструкция Peltor/Comhead содержит отдельные таблицы для `MT15H69FB-**` (ComTac II) и `MT15H69FB-**` (ComTac XS). Это не официальный хост 3M, но PDF идентифицирует авторизованного Peltor-дистрибьютора и содержит английскую страницу технических данных. Источник: [Peltor ComTac XP/XS instruction, PDF](https://www.comhead.de/media/pdf/b5/5b/9c/00172_Peltor_Comtac_XP_XS_Anleitung_1268754380.pdf), стр. 2 (PDF page 2; English technical page is printed EN 8).

Для **ComTac II MT15H69FB** (folding headband, 343 g; `FB` не backband) напечатано: частоты 125/250/500/1000/2000/3150/4000/6300/8000 Hz; MV 14.5/17.7/26.3/31.3/29.8/36.7/35.1/37.5/35.4 dB; SD 3.0/2.9/2.8/2.6/3.2/2.7/2.5/2.8/3.0 dB; NRR 21 dB, CSA class B. Это таблица **NRR**, APV не напечатан; не вычислять APV самостоятельно и не выдавать NRR за SNR. В той же странице отдельно напечатана SNR-таблица **ComTac XS** (SNR 26, H 31, M 24, L 16), которую нельзя переносить на ComTac II.

**Известно:** ранняя двухчашечная радиогарнитура; в каталоге есть варианты backband/headband и разные downlead/микрофоны.

**Не найдено для ComTac II:** цифровой gain/EQ, limiter/output ceiling, attack/release, latency, ambient-mic frequency response и IP. В инструкции дополнительно есть B3 `Noise levels` (график связи внешнего и внутреннего dB(A)), B4 `Input signal / usage time` (график допустимого входа во времени) и B5 `Sound exposure when using AUX input` (график AUX mVrms → внутренний dB(A)); графики полезны как производительская область/transfer evidence, но не оцифрованы и не дают точной АЧХ или peak ceiling. Не превращать их в один жёсткий лимит 82 dB(A). Каталог 3M дополнительно подтверждает семейство и конфигурации (например, `MT15H69BB-19`, split/backband, зелёные чашки, dual ComTac II): [3M PELTOR Sound Catalog](https://www.digikey.com/htmldatasheets/production/2099570/0/0/1/peltor-sound-catalog.html).

## Tactical Sport / SportTac (MT16H210F-*)


Официальная инструкция 3M, `FP3584_SportTac.pdf`, указывает массу 318 g и следующие лабораторные данные для `MT16H210F-*`; стандарт маркировки — SNR (европейская таблица): [3M FP3584 SportTac](https://multimedia.3m.com/mws/media/940033O/fp3584-sporttac-pdf.pdf?fn=FP3584_SportTac.pdf).

| f, Hz | 125 | 250 | 500 | 1000 | 2000 | 4000 | 8000 |
|---|---:|---:|---:|---:|---:|---:|---:|
| MV, dB | 12.1 | 17.9 | 27.0 | 26.8 | 30.5 | 38.3 | 36.4 |
| SD, dB | 4.3 | 3.1 | 3.8 | 3.0 | 3.0 | 3.7 | 5.4 |
| APV, dB | 7.8 | 14.8 | 23.2 | 23.8 | 27.5 | 34.6 | 31.0 |

SNR 26 dB; H 29 dB; M 23 dB; L 16 dB. Критические уровни электронной схемы: H=113 dB(A), M=104 dB(A), L=91 dB(A). Это уровни входного сигнала/классификации, не максимальный выход на динамике. Есть level-dependent усиление слабых звуков, подавление громких, внешний audio input и auto shutoff; численной АЧХ усилителя, gain, attack/release и latency 3M в листе не публикует.

Цвет, крепление и игровой вариант: найденные листы описывают `MT16H210F-*`, а не конкретный Tarkov-скин. Цвет/артикул, гелевые чашки и helmet-mount нельзя выводить из таблицы SportTac.

## ComTac IV Hybrid (MT16H044F-**)

Источник: [3M ComTac IV technical datasheet](https://multimedia.3m.com/mws/media/1142194O/3m-peltor-comtac-iv-headset-datasheet-pdf.pdf) и [3M Military Catalogue 2017](https://multimedia.3m.com/mws/media/1412855O/3m-peltor-military-catalogue-2017.pdf). Артикулы: `MT16H044F-38 GN` (Peltor wired), `-86 GN` (NATO wired), `-88 GN` (dual 1× downlead), olive green; 2×AAA; ~250 h; 238 g с батареями; operating −40…+55 °C, storage −50…+70 °C; MIL-STD-810F и EMC-MIL-STD-461F.

`PELTIP4-01` UltraFit:

| f, Hz | 125 | 250 | 500 | 1000 | 2000 | 4000 | 8000 |
|---|---:|---:|---:|---:|---:|---:|---:|
| MV | 28.6 | 27.0 | 30.3 | 29.2 | 30.9 | 31.6 | 32.0 |
| SD | 5.3 | 6.0 | 6.0 | 5.4 | 3.4 | 6.6 | 4.5 |
| APV | 23.3 | 21.0 | 24.4 | 23.8 | 27.4 | 25.1 | 27.6 |

SNR 27; H 26; M 25; L 23 dB.

`PELTIP5-01` Torque:

| f, Hz | 125 | 250 | 500 | 1000 | 2000 | 4000 | 8000 |
|---|---:|---:|---:|---:|---:|---:|---:|
| MV | 31.0 | 29.5 | 31.4 | 33.9 | 33.7 | 40.0 | 42.4 |
| SD | 4.9 | 4.6 | 5.2 | 5.5 | 3.6 | 2.5 | 3.4 |
| APV | 26.1 | 24.9 | 26.3 | 28.4 | 30.1 | 37.4 | 38.9 |

SNR 32; H 32; M 29; L 27 dB. Это hybrid-вкладыши, поэтому значения не являются over-ear ComTac attenuation.

Характеристики электроники: level-dependent ambient listening, функции release time/balance/boost mode, detachable speech microphone, power-fail-safe и automatic power-off. Точные gain, frequency response, output ceiling, latency и release-time в миллисекундах не опубликованы.

## ComTac V (MT20H682)

Официальная инструкция ComTac V подтверждает модель `MT20H682` и отдельные helmet-методы измерения; доступный технический лист с полными таблицами для V/XPI опубликован как ComTac XPI и использует те же `MT20H682` варианты. Не смешивать `BB` (backband) и `FB` (foldable headband). Источники: [3M ComTac V user instructions](https://www.manualslib.com/manual/2180471/3m-Peltor-Comtac-V.html), [3M ComTac XPI datasheet](https://multimedia.3m.com/mws/media/2367227O/3m-peltor-comtac-xpi-headset-datasheet-emea.pdf), [3M ComTac XPI standard-02 datasheet](https://multimedia.3m.com/mws/media/1180561O/3m-peltor-comtac-xpi-standard-02-technical-datasheet.pdf).

Для `MT20H682FB-*` (foldable, foam cushion), 3M публикует:

| f, Hz | 125 | 250 | 500 | 1000 | 2000 | 4000 | 8000 |
|---|---:|---:|---:|---:|---:|---:|---:|
| MV, dB | 11.5 | 17.9 | 27.8 | 30.0 | 32.1 | 36.2 | 40.3 |
| SD, dB | 2.5 | 2.7 | 1.8 | 2.3 | 3.0 | 2.0 | 3.1 |
| APV, dB | 9.0 | 15.3 | 25.9 | 27.7 | 29.1 | 34.2 | 37.2 |

SNR 28; H 31; M 25; L 18 dB. Для `MT20H682BB-*` (backband): MV 16.6/16.8/27.4/32.1/33.1/33.0/36.1; SD 1.8/2.3/2.2/2.1/3.2/2.9/3.2; APV 14.8/14.5/25.2/30.0/29.9/30.1/32.9 dB на тех же частотах; SNR 28, H 31, M 25, L 18 dB.

Источник также указывает ~200 h для ComTac XPI, 340 g ±5% без батарей для `MT20H682FB-38`, 2×AAA, storage −20…+55 °C; масса зависит от модели и батарей. Это данные XPI/EMEA и не следует автоматически приписывать каждому US ComTac V.

Электронные функции V/XPI: environmental listening, voice-guided menu, balance/boost/release-time controls, audio input и power-fail-safe. Численные gain, attack/release, latency, output ceiling, directionality/stereo и microphone frequency response не приведены. В V manual есть отдельные результаты с Ceradyne/OPSCOR helmet; это fit/helmet transfer, не универсальная чашечная АЧХ.

## ComTac VI / VI NIB

Источник: [3M ComTac VI NIB and SWAT-TAC VI NIB brochure](https://multimedia.3m.com/mws/media/1662214O/3m-peltor-comtac-vi-nib-and-swat-tac-vi-nib.pdf) и [3M Defence catalogue, attenuation tables](https://multimedia.3m.com/mws/media/2410397O/3m-defence-and-public-safety-catalogue-emea-english.pdf). Таблицы ниже — только `MT20H682FB-*N*` (NIB, foldable headband), с конкретным seal.

**Foam cushion:** MV 11.6/17.6/30.5/29.7/29.4/32.8/38.3; SD 2.3/2.1/2.9/2.3/3.2/2.1/4.8; APV 9.3/15.5/27.6/27.4/26.2/30.7/33.5 dB (125…8000 Hz). H/M/L/SNR = 31.2/26.7/18.9/28.5 dB. Масса 358 g.

**HY80 gel cushion:** MV 15.1/18.2/26.2/32.2/30.4/29.3/36.7; SD 3.4/3.0/2.2/2.5/3.7/3.7/4.0; APV 11.7/15.2/24.0/29.7/26.7/25.6/32.7 dB. H/M/L/SNR = 30.4/27.1/20.8/28.6 dB. Масса 403 g.

**Dual protection с E-A-R Classic:** MV 31.8/39.5/52.5/43.8/40.8/50.8/46.4; SD 8.6/9.3/8.2/5.1/5.0/3.1/3.9; APV 23.2/30.2/44.3/38.8/35.8/47.7/42.5 dB. H/M/L/SNR = 42.4/41.8/38.3/42.9 dB. Это измерение комбинации, не свойства одних чашек.

NIB: варианты 915.5 MHz (US/FCC), dual-frequency 915.5/864 MHz для отдельных US Federal Government поставок; не считать это звуковым Bluetooth или внутриигровой stereo-спецификацией. 3M заявляет до 4 full-duplex talkers и до 60 слушателей в пределах до 30 ft line-of-sight для NIB; это communication-radio claim, а не HRTF/directionality. Для VI указаны батареи 2×AAA и варианты single/dual comm, Peltor/NATO/stereo downlead; цветовые варианты обычно GN (olive green), CY (coyote), SV/black в зависимости от артикула. Точные per-channel gain, EQ, limiter ceiling, attack/release и latency не опубликованы.

### Отдельная таблица ComTac V из US manual

В руководстве ComTac V `34-8725-1903-7`, стр. 4, есть таблицы именно для `MT20H682`, а также варианты folding/backband/helmet attachment и foam/gel cushions. Для folding headband + foam среднее затухание на 125/250/500/1000/2000/3150/4000/6300/8000 Hz: 13.6/17.7/28.8/31.9/32.3/39.3/39.9/42.1/41.4 dB; NRR 23, CSA B. Для helmet attachment + foam (измерение с 3M Ceradyne ballistic helmet): 20.3/26.4/32.1/33.0/33.4/39.7/34.1/39.4/40.1 dB; NRR 22, CSA A. SD/APV в архивной web-рендеризации не удалось надёжно сопоставить по строкам, поэтому они оставлены неизвестными в JSON. Helmet table — fit/helmet transfer, не универсальная характеристика крепления или EQ. Источник: [3M PELTOR ComTac V User Instructions, page 4](https://www.manualslib.com/manual/2180471/3m-Peltor-Comtac-V.html?page=4).

## TEP-300 (in-ear Tactical Earplug)

Источники: [3M TEP-300 US/AC technical data](https://multimedia.3m.com/mws/media/2208992O/3m-peltor-technical-data-specification-tep-300-usac.pdf), [3M TEP-300 EU datasheet](https://multimedia.3m.com/mws/media/1854243O/3m-peltor-techn-datasheet-tep-300-eu.pdf?fn=3M-PELTOR-Techn-Datasheet-TEP-300-EU_R1.pdf). В US-листе таблицы зависят от насадки:

| Насадка / f Hz | 125 | 250 | 500 | 1000 | 2000 | 3150 | 4000 | 6300 | 8000 | NRR |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| CCC-GRM-25 MV | 35.0 | 31.8 | 37.4 | 37.4 | 36.0 | 39.2 | 40.4 | 44.9 | 45.8 | 27 |
| CCC-GRM-25 SD | 4.6 | 4.2 | 5.2 | 5.1 | 3.3 | 4.9 | 4.8 | 3.6 | 2.9 | — |
| Skull Screw MV | 35.7 | 35.0 | 40.7 | 39.0 | 38.3 | 41.7 | 41.4 | 44.0 | 45.9 | 30 |
| Skull Screw SD | 5.4 | 5.2 | 5.4 | 3.9 | 2.5 | 4.4 | 3.7 | 4.1 | 4.1 | — |
| UltraFit MV | 34.3 | 31.9 | 35.2 | 34.1 | 34.5 | 38.6 | 35.5 | 38.2 | 39.3 | 23 |
| UltraFit SD | 6.1 | 6.1 | 6.1 | 5.2 | 4.8 | 4.4 | 3.9 | 3.9 | 3.0 | — |

EU-лист (`TEP-300 EU`, стр. 2, EN 352-2:2020) публикует три полные таблицы. Для Skull Screw/Torque: MV 38.1/35.5/40.7/40.9/37.5/40.3/44.9; SD 5.4/5.4/5.7/5.8/3.2/3.3/4.2; APV 32.7/30.1/35.0/35.1/34.3/37.0/40.7 dB. Отдельная строка H/M/L/SNR: MV 38.4/38.5/37.5/39.7, SD 2.5/3.1/4.1/2.6, APV 36/35/33/37. Для CCC-GRM-25: MV 35.1/32.5/37.5/37.9/37.1/40.8/44.6; SD 5.5/4.9/5.1/4.8/4.3/4.6/4.7; APV 29.6/27.6/32.4/33.1/32.8/36.2/39.9; H/M/L/SNR MV 38.0/36.9/34.9/38.5, SD 3.4/3.6/4.0/3.3, APV 35/33/31/35. Для UltraFit: MV 32.8/30.2/31.6/31.2/33.0/34.4/37.9; SD 6.2/4.5/5.4/4.4/4.0/4.8/3.9; APV 26.6/25.7/26.2/26.8/29.0/29.6/34.0; H/M/L/SNR MV 33.1/31.4/30.8/33.4, SD 3.5/3.7/4.0/3.5, APV 30/28/27/30. Масса одного вкладыша 4.7 g, масса кейса с батареями и двумя вкладышами 188 g; IP68, вкладыш −20…+50 °C, зарядный кейс 0…+45 °C. NFMI: 596 kbit/s, 9.8–11.7 MHz, около 50 cm. Источник: [3M TEP-300 EU datasheet](https://multimedia.3m.com/mws/media/1854243O/3m-peltor-techn-datasheet-tep-300-eu.pdf?fn=DS17043_TEP_300_EU.pdf). US NRR и EU SNR не взаимозаменяемы.

TEP-300: rechargeable lithium-ion earplugs; EU sheet указывает ~10 h per charge, 3M TEP-300AD AAA adapter, environmental microphones, in-ear speech microphone, NFMI wireless intercom; US materials также описывают CCC/Skull Screw/UltraFit tips. IP, operating temperature и цвет нужно брать из конкретной региональной ревизии/артикула (GE grey, CY coyote, TN tan встречаются в каталоге); не переносить US NRR на EU SNR. 3M не публикует usable output ceiling, transfer function, gain по частотам, attack/release и latency. NFMI carrier frequency/bit-rate, встречающиеся у дилеров, не являются подтверждённой акустической АЧХ и в расчёт не включены.

## Конфликты и пригодность

1. Один и тот же индекс `MT20H682` покрывает ComTac V/XPI и VI NIB-производные; суффиксы `BB`, `FB`, `N`, `02/38/86/88`, подушка и регион обязательны. Таблица VI NIB не является таблицей любого ComTac V.
2. ComTac IV Hybrid измеряется через UltraFit/Torque eartips; ComTac V/VI — через чашки, а TEP-300 — внутриканально. Складывать их attenuation нельзя без отдельной измерительной процедуры.
3. US NRR, EN SNR и H/M/L имеют разные методики и маркировочные смыслы. Ни один из них не является dBFS gain или Unity EQ.
4. Производитель не публикует для этих семей точные цифровые transfer functions, channel matching, limiter/output ceiling, latency и attack/release curves. Эти поля остаются неизвестными и должны заполняться только измерениями реального устройства/сцены.
5. Для будущих игровых расчётов безопасный минимум — использовать только частотные точки MV/SD/APV как справочную защитную кривую, хранить `source_variant`, `tip/cushion`, `standard` и `claim_type`, а субъективный результат закрывать controlled live-raid прослушиванием.
