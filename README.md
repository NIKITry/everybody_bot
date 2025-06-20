# Telegram Bot

Telegram бот для управления списком пользователей в групповых чатах. Бот был разработан с помощью Claude 3.7 Sonnet, продвинутой модели искусственного интеллекта от Anthropic.

## Установка

1. Клонируйте репозиторий:
```sh
git clone https://github.com/NIKITry/everybody_bot.git
cd everybody_bot
```

2. Установите зависимости:
```sh
dotnet restore
```

3. Создайте папку для базы данных:
```sh
mkdir -p data
```

## Функциональность

- Добавление пользователей в список
- Тег всех пользователей из списка
- Очистка списка
- Случайные ответы на сообщения
- Выбор случайного длинного слова из сообщения
- Грубый режим с настраиваемым словом

## Переменные окружения

- `TelegramBotToken` - токен бота от @BotFather
- `TARGET_WORD` - слово, которое будет добавляться к случайному слову (по умолчанию "людей")
- `PROBABILITY` - вероятность ответа на сообщение (от 1 до 100)

## Запуск

### Локально

```sh
TelegramBotToken="YOUR_BOT_TOKEN" PROBABILITY="100" dotnet run
```

### В Docker

```sh
docker build -t telegram-bot .
docker run -d --name telegram-bot \
  -e TelegramBotToken="YOUR_BOT_TOKEN" \
  -e TARGET_WORD="людей" \
  -e PROBABILITY="100" \
  -v ./data:/app/data \
  telegram-bot
```

## Команды

- `/start` - начало работы с ботом
- `/add` - добавить пользователя в список
- `/tag` - отметить всех пользователей из списка
- `/clear` - очистить список
- `/rude_mode_enable` - включить грубый режим
- `/rude_mode_disable` - выключить грубый режим
- `/set_rude_word` - установить слово для грубого режима (до 20 символов, только буквы)

## Разработка

Этот проект был разработан с использованием:
- .NET 8.0
- Telegram.Bot 22.0.0
- Microsoft.Data.Sqlite
- Dapper
- Serilog

Весь код был написан с помощью Claude 3.7 Sonnet, который помог с:
- Структурой проекта
- Реализацией функциональности
- Обработкой ошибок
- Оптимизацией кода
- Написанием документации

## Проверка базы данных

Для просмотра содержимого базы данных используйте команду `sqlite3`:

```sh
# Установка sqlite3 (если еще не установлен)
# macOS:
brew install sqlite3
# Ubuntu/Debian:
sudo apt-get install sqlite3

# Просмотр содержимого базы данных
sqlite3 data/bot.db

# Полезные команды в sqlite3:
.tables                    # показать все таблицы
.schema user_settings     # показать структуру таблицы user_settings
SELECT * FROM users;      # показать всех пользователей
SELECT * FROM user_settings;  # показать настройки пользователей
.quit                     # выйти из sqlite3
``` 