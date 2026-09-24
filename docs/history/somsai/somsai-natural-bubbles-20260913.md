# SOMSAI: прежние пузыри перехода и живой фон

Обновление `SomsOceanNoRays`: удалены пять треугольных лучей, рисовавшихся поверх иллюстрации. Анимированные пузыри и свет в самой картинке сохранены. Сборка и визуальная проверка прошли; снимок `.test-tmp/soms-ocean-no-rays-ui/14-ocean-animation-start.png`. Текущая DLL: 17 053 696 байт, SHA256 `37da31b5e87585089a8de4fa0ec7c6b659e34bda5982e790ba7db6389481ac4f`. Предыдущая версия с конфигами сохранена в `.test-tmp/soms-ocean-no-rays-release/published-before/`.

Вернулись исходные 104 основных пузыря перехода: прежние координаты, порядок наложения и диапазон диаметров 0.75–1.5 от базового размера. При подготовке перехода проверяется покрытие экрана. Только в непокрытых местах добавляются меньшие пузыри позади основных. Раскладка рассчитывается при изменении размера, а не каждый кадр. На 1280×720 понадобилось 107 дополнительных пузырей, на 1024×768 — 77, на 2560×1080 — 43, на 720×1280 — один. Проверка включает края и углы. Навигация по-прежнему выполняется под закрытым экраном.

Фоновая картинка очищена от статичных пузырей. Поверх неё анимируются 34 отдельные прозрачные сферы с бликами, преимущественно по краям. Они медленно поднимаются, покачиваются, выходят за верхнюю границу и возвращаются снизу. Цикл занимает 45–94.5 секунды, начальные фазы различаются. Используются одни и те же объекты без постоянного создания частиц. При уходе с экрана анимация останавливается.

## Изображение

Использован встроенный imagegen, режим редактирования. Исходная иллюстрация сохранена в репозитории, но исключена из ресурсов DLL. Игра загружает [ocean-background-clear.png](../../../client/enhanced-auth/osu.Game.Rulesets.EnhancedAuth/Resources/SomsAi/ocean-background-clear.png). Рыбки, арена и подводное оформление сохранены; пузырьки рисуются кодом.

Полный запрос для изображения:

> Edit this existing game background with strict preservation. Remove ALL individual floating soap/air bubbles (the transparent spherical objects with white circular rims and white highlights) everywhere, including the left and right decorative seaweed margins, water around both fish, center water, upper water, and bottom. Inpaint the removed circles with the matching underlying water, arena architecture, seaweed/coral. Preserve the two fish mascots exactly (catfish with sombrero at upper left and puffer fish upper right), their shapes, poses, colours, placement, all underwater arena architecture, flags, coral, shells, seabed, existing water-surface caustics and sunlight rays, and the full image composition and aspect ratio. Do not add new bubbles or other objects. Do not blur, restyle, recolour, crop, add text, or change the layout. This is a clean background plate for a game: all bubbles will be rendered as moving sprites in code, so no static circular bubbles must remain. Output an opaque PNG with the same landscape composition.

## Проверка и установка

Сборка `SomsBubblesNatural`: 17 054 208 байт, SHA256 `208928fb79e885d97dc49d4136565fc0643e7574a70780733f93cf500a1b413f`.

В настоящем клиенте на отдельном профиле прошли проверки прежних размеров и количества основных пузырей, сплошного покрытия четырёх пропорций экрана, ожидания загрузки и завершения перехода, подъёма фоновых пузырей и их возвращения снизу. Снимки до/после движения: `.test-tmp/soms-bubbles-natural-ui/14-ocean-animation-start.png` и `15-ocean-animation-later.png`; покрытие: `12-bubble-cover.png`. Лог: `.test-tmp/soms-bubbles-natural-ui.log`.

Опубликованы DLL и согласованные хеши двух конфигов лаунчера. Серверные службы не перезапускались. Предыдущие файлы сохранены в `.test-tmp/soms-bubbles-natural-release/published-before/`. Для отката восстанавливать DLL и оба конфига вместе.
