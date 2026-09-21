# SOMSAI: цвета персонажей по оригиналам

Цвета сома и фугу на фоне скорректированы по двум оригинальным изображениям из сообщения пользователя. У сома приглушён оранжевый оттенок, тело стало нейтральнее, плавники — мягче по персиковым тонам. Фугу получил менее насыщенный золотисто-песочный цвет и прохладный светлый низ. Анимация пузырей и код переходов не менялись.

Готовый фон: [ocean-background-matched.png](../.sources/LazerAuthlibInjection/osu.Game.Rulesets.EnhancedAuth/Resources/SomsAi/ocean-background-matched.png). Использован встроенный imagegen в режиме редактирования. Предыдущая иллюстрация сохранена в исходниках; в DLL включена только текущая версия фона.

## Запрос для imagegen

> Precise colour correction of an existing game background. EDIT TARGET: the wide landscape underwater arena image with a sombrero catfish at upper left and a puffer fish at upper right, no static bubbles. COLOUR REFERENCES ONLY: the two separate original character images supplied by the user (large grey-brown catfish in a yellow sombrero, and muted gold/tan puffer fish). Keep the target's entire composition, exact fish silhouettes, poses, expressions, eyes, whiskers, fins, scales, spikes, linework, positions and sizes. Change ONLY the colours/tonal grading of the two fish in the wide image to match those original references. Catfish: restrained neutral taupe/grey-brown body, softly shaded warm ivory muzzle/belly, pale peach/salmon fins rather than vivid orange, muted burgundy mouth; match the yellow/olive-green/red sombrero palette from the original without oversaturating it. Puffer fish: subdued sandy gold, ochre and tan upper body rather than saturated orange, soft cream transition and cool light blue-grey underside/fins; gently shaded dark brown features. Use the actual original-reference colours, not a uniform grey wash. Reduce the harsh orange saturation and hard highlights of these two characters. Preserve readability and original reference shading/contrast. DO NOT change the surrounding turquoise water, arena architecture, light rays already in the painting, coral, flags, seabed, shells or any pixels outside the two character silhouettes. Do not move or redraw the fish, do not replace their poses with the reference poses, do not add static bubbles, new rays, text, effects or objects. Output the corrected full-width opaque PNG background in the same aspect ratio and layout.

## Сборка

`SomsOceanColourMatch`, 17 024 000 байт. SHA256: `4a0287bf2d0ea33ae889f97755b3e5b7c6957da5b78ac7095be42b5237f58afc`.

Опубликовано. Проверка настоящего клиента прошла; внешний HTTPS-контроль подтвердил новый манифест, SHA256 скачанной DLL и здоровье сайта. Лог публикации: `.test-tmp/soms-ocean-colour-match-release/public-check.log`. Для обновления нужно перезапустить SOMS через свитчер.

Проверки клиента и снимки: `.test-tmp/soms-ocean-colour-match-ui/`, лог `.test-tmp/soms-ocean-colour-match-ui.log`. Резервная копия DLL и двух конфигов лаунчера перед публикацией: `.test-tmp/soms-ocean-colour-match-release/published-before/`. Откат выполняется восстановлением этих трёх файлов вместе.
