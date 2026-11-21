# PawnIO modules

Official signed modules of the PawnIO driver, release **0.2.12**, taken unmodified from
<https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.12> (`release_0_2_12.zip`).

- Licence: **LGPL-2.1** (see `COPYING`); source code: <https://github.com/namazso/PawnIO.Modules>.
- They are embedded into `FoxideHelper.exe` as resources and loaded into the PawnIO driver
  (<https://pawnio.eu>, installed separately by the user). Foxide Helper talks to the driver only through its
  DeviceIoControl interface, as allowed by the driver's licence exception.

| Module | Used for |
|---|---|
| IntelMSR | Intel CPU core / package temperature (IA32_THERM_STATUS, TjMax) |
| AMDFamily17 | AMD Zen (family 17h/19h/1Ah) temperature via SMN |
| AMDFamily10, AMDFamily0F | older AMD CPUs |
| LpcIO | motherboard Super I/O chip: fans, temperatures, fan control |
| SmbusI801, SmbusPIIX4 | SMBus (Intel / AMD) for memory module temperature sensors |
| DellSMM | Dell laptop fans (speed, level) and temperatures through the Dell SMM BIOS interface |
