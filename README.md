# WMac

**Un escritorio moderno para Windows 11: widgets, efecto cristal y tus iconos de siempre, organizados en páginas.**

WMac se muestra detrás de tus ventanas, sobre el escritorio de Windows. Al cerrarlo, todo vuelve a estar exactamente como lo dejaste. La barra de tareas, el menú Inicio y el Explorador siguen funcionando como siempre.

## Características

- **Fondo de cristal.** Tu fondo de pantalla real con un desenfoque suave y tarjetas translúcidas que se adaptan a sus colores.
- **Widgets.** Reloj analógico y lo que suena ahora mismo, con portada y controles, a la izquierda; clima y calendario a la derecha.
- **Iconos en páginas.** Una cuadrícula centrada de iconos grandes. Pasas de página con la rueda, el panel táctil o el teclado.
- **Búsqueda instantánea.** Empieza a escribir en cualquier momento. La búsqueda incluye lo que hay dentro de los grupos y filtra por categoría.
- **Grupos visuales.** Agrupa iconos arrastrando uno sobre otro, o usa la organización automática por categorías. Los grupos solo cambian lo que ves: tus archivos se quedan donde están.
- **Multimonitor y escalado.** Respeta la resolución, el escalado de cada monitor y el área de trabajo.
- **Ligero.** En reposo no hay animaciones, y los iconos y archivos se leen en segundo plano.

## Seguridad primero

WMac no mueve, oculta ni modifica tus iconos, archivos, fondo de pantalla, registro ni el Explorador. Dibuja su propia capa encima y deja intacto el escritorio de Windows que hay debajo.

- **Respaldo automático.** Antes de cada activación guarda la posición de cada icono, la vista y la lista de archivos del Escritorio. Si no puede guardarlo, no se activa.
- **Comprobación.** Al desactivarlo, al restaurar y al desinstalar, compara tu escritorio con el respaldo. Si algún icono no está en su sitio (por ejemplo, porque Windows lo recolocó al cambiar la resolución), te ofrece devolverlo.
- **Protección de archivos.** Viene activada: WMac solo abre y copia, nunca elimina, renombra ni mueve archivos.
- **Retirada automática.**
  - La primera vez que lo activas, si no confirmas en 15 segundos, vuelve tu escritorio original.
  - Si WMac falla o se cierra de golpe, su capa desaparece con el proceso y no se reactiva solo.
  - Si la interfaz deja de responder, un vigilante interno cierra WMac a los 25 segundos.

## Instalación

1. Descarga `WMac-Setup-x.y.z.exe` desde [Releases](../../releases).
2. Ejecútalo. Se instala solo para tu usuario, en `%LOCALAPPDATA%\Programs\WMac`, sin permisos de administrador.
   - El ejecutable no está firmado. Si SmartScreen avisa, pulsa **Más información › Ejecutar de todas formas**.
3. Abre **WMac** desde el menú Inicio. Puedes probarlo primero en una ventana (**Vista previa**) o **Activar escritorio**.
4. Si te gusta cómo se ve, pulsa **Mantener**. WMac lo recordará y se abrirá con Windows. Puedes cambiarlo en **Ajustes › Inicio**.

**Requisitos:** Windows 11 (también funciona en Windows 10) y .NET Framework 4.8, que ya viene con Windows.

## Volver al escritorio original

- **Bandeja › Restaurar escritorio original**, o **menú Inicio › WMac › Restaurar escritorio original**, que funciona aunque WMac no responda. WMac se cierra, deja de iniciarse con Windows y comprueba que cada icono siga en su sitio.
- Los respaldos se guardan en `%LOCALAPPDATA%\WMac\Respaldos`. La carpeta incluye un `LEEME.txt` con los pasos para restaurar incluso sin WMac.
- **Desinstalar:** Configuración › Aplicaciones › WMac Desktop. Se borran el programa, sus accesos y sus ajustes; los respaldos del escritorio se conservan.

### Línea de comandos

| Opción | Qué hace |
|---|---|
| `--preview` | Abre WMac en una ventana, sin tocar el escritorio. |
| `--restore` | Restaura el escritorio original y desactiva el inicio con Windows. |
| `--check-layout` | Compara tu escritorio con el último respaldo (solo lectura). |
| `--backup-layout` | Guarda un respaldo ahora (solo lectura). |
| `--restore-layout [archivo.json]` | Devuelve los iconos a las posiciones de un respaldo (pide confirmación). |
| `--uninstall` | Desinstala WMac. |

## Compilar

Requisitos: Windows y el [SDK de .NET](https://dotnet.microsoft.com/download) 6 o posterior. Los proyectos usan .NET Framework 4.8 con WPF, y el SDK descarga los paquetes de NuGet al compilar.

```powershell
git clone https://github.com/BlackCrowAgency/wmac-blackcrow.git
cd wmac
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` compila la app y el instalador, y deja el resultado en `dist\`:

- `dist\WMac-Setup-x.y.z.exe`: el instalador, que lleva la app incrustada.
- `dist\portable\WMac.exe`: la app sin instalar.

Para compilar solo la app y probarla en una ventana:

```powershell
dotnet build src\WMac\WMac.csproj -c Release
.\src\WMac\bin\Release\net48\WMac.exe --preview
```

## Estructura

| Ruta | Contenido |
|---|---|
| `src/WMac/Core` | Modelo del escritorio, iconos del Shell, fondo de pantalla, multimedia, clima, ajustes, respaldo y restauración. |
| `src/WMac/Desktop` | Integración con el escritorio de Windows: una ventana por monitor sobre la vista de iconos del Explorador. |
| `src/WMac/Ui` | Interfaz WPF: lanzador, widgets, carpetas, menús, ajustes y bandeja. |
| `src/WMac.Setup` | Instalador por usuario, sin dependencias externas. |
| `tools/make-icon.ps1` | Genera el icono de la app (`src/WMac/Assets/WMac.ico`). |
| `build.ps1` | Compila todo y prepara `dist\`. |

## Datos y privacidad

- WMac guarda sus ajustes, su registro y los respaldos del escritorio en `%LOCALAPPDATA%\WMac`.
- Solo se conecta a internet si configuras el widget del clima. En ese caso consulta [Open-Meteo](https://open-meteo.com), que no pide cuenta, con el nombre de la ciudad y después sus coordenadas.
- No envía estadísticas ni datos de uso.

## Licencia

WMac es software libre bajo la licencia [GNU General Public License v3.0](LICENSE). Puedes usarlo, estudiarlo, modificarlo y redistribuirlo. Si distribuyes una versión modificada, debes publicar también su código fuente bajo la misma licencia.

---

WMac es un proyecto independiente. No está afiliado a Apple ni a Microsoft. macOS es una marca de Apple Inc.; Windows es una marca de Microsoft Corporation.
