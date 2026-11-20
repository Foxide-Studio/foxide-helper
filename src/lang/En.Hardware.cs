namespace FoxideDisk
{
    public static partial class L
    {
        static readonly string[] EnHardware =
        {
            // Hardware.cs — sensors, fans
            "Windows не отдаёт температуру процессора без драйвера", "Windows does not expose the CPU temperature without a driver",
            "Температура процессора доступна только с правами администратора (и не на всех ПК)", "The CPU temperature is available only with administrator rights (and not on every PC)",
            "Температура ACPI-зоны: близка к процессорной, но это не датчик ядра", "ACPI thermal zone temperature: close to the CPU's, but not a core sensor",
            "Датчик гибридного процессора AMD (общий кристалл с графикой)", "AMD APU sensor (the die is shared with the graphics)",
            "Нет датчика: температуру модулей памяти можно прочитать только через шину SMBus, а для этого нужен драйвер", "No sensor: memory module temperature can only be read over the SMBus, which needs a driver",
            "Датчик SMBIOS (Win32_TemperatureProbe)", "SMBIOS sensor (Win32_TemperatureProbe)",
            "Вентилятор видеокарты", "Graphics card fan",
            "Вентилятор видеокарты {0}", "Graphics card fan {0}",
            "Системный вентилятор", "System fan",
            "Вентилятор (WMI)", "Fan (WMI)",
            "Есть, скорость не сообщает", "Present, does not report its speed",
            "Ошибка устройства (код {0})", "Device error (code {0})",
            "Нужен запуск от имени администратора", "Run as administrator to control it",
            "Только чтение: управление вентиляторами AMD не поддерживается", "Read-only: AMD fan control is not supported",
            "Управление вентиляторами доступно только для видеокарт NVIDIA", "Fan control is available only for NVIDIA graphics cards",
            "Видеокарта не найдена", "Graphics card not found",
            "Датчики уже закрыты", "Sensors are already closed",
            "Библиотека NVIDIA (nvml.dll) недоступна", "The NVIDIA library (nvml.dll) is not available",
            "Библиотека NVIDIA не инициализирована", "The NVIDIA library is not initialized",
            "Неверный номер видеокарты или вентилятора", "Invalid graphics card or fan number",
            "Драйвер NVIDIA не поддерживает ручное управление этим вентилятором", "The NVIDIA driver does not support manual control of this fan",
            "Драйвер NVIDIA отказал в доступе (нет прав)", "The NVIDIA driver denied access (no permission)",
            "Эта версия драйвера NVIDIA не умеет управлять вентиляторами", "This NVIDIA driver version cannot control fans",
            "Видеокарта NVIDIA перестала отвечать", "The NVIDIA graphics card stopped responding",
            "Ошибка NVML {0}", "NVML error {0}",
            "вент. {0} %", "fan {0} %",
            "вент. {0} об/мин", "fan {0} RPM",
        };
    }
}
