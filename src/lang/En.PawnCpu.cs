namespace FoxideDisk
{
    public static partial class L
    {
        static readonly string[] EnPawnCpu =
        {
            // Pawn/PawnCpu.cs — CPU sensors through PawnIO
            "Датчики процессора через PawnIO есть только для процессоров Intel и AMD.", "CPU sensors through PawnIO are available only for Intel and AMD processors.",
            "Этот процессор AMD не поддерживается: нужен Ryzen / EPYC (Zen) или новее.", "This AMD processor is not supported: Ryzen / EPYC (Zen) or newer is required.",
            "Не удалось загрузить модуль PawnIO для процессора:", "Could not load the PawnIO module for the processor:",
            "Процессор не отдал ни одного датчика через PawnIO.", "The processor did not return any sensor through PawnIO.",
            "Процессор (корпус)", "CPU Package",
            "Ядра процессора", "CPU Cores",
            "Встроенная графика", "Integrated Graphics",
            "Ядра (макс.)", "Cores (max)",
            "P-ядро #{0}", "P-Core #{0}",
            "E-ядро #{0}", "E-Core #{0}",
            "Ядро #{0}", "Core #{0}",
            "CCD{0} (Tdie)", "CCD{0} (Tdie)",
        };
    }
}
