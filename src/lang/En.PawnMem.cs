namespace FoxideDisk
{
    public static partial class L
    {
        static readonly string[] EnPawnMem =
        {
            // Pawn/PawnMemory.cs — memory module temperatures over SMBus
            "Модуль памяти", "Memory module",
            "порт {0}", "port {0}",
            "Контроллер SMBus не поддерживается (нужен Intel PCH или AMD FCH).", "SMBus controller is not supported (Intel PCH or AMD FCH is required).",
            "Шина SMBus занята другой программой.", "The SMBus is busy with another program.",
            "Датчиков температуры на модулях памяти не найдено (у многих модулей их нет).", "No memory module temperature sensors found (many modules do not have one).",
            "Модулей памяти на шине: {0}, но датчика температуры у них нет (у многих модулей его нет).", "Memory modules on the bus: {0}, but they have no temperature sensor (many modules do not have one).",
        };
    }
}
