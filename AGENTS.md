# asset tool — 인수인계 문서 (AGENTS.md)

> 작성일 2026-11-15. 폴더의 코드·프로젝트 파일·기존 문서와 실제 게임 설치 폴더(Steam `.acf`, `UnityPlayer.dll`, `MelonLoader.dll`)를 읽어 정리했다.
> 이 문서 작성 시점에 인게임 재검증은 하지 않았다. **"실측"** 표시는 디스크에서 직접 읽은 값, **"미확인"**은 다음 세션에서 확인할 항목이다.

## 0. 프로젝트 개요

- **무엇인가:** Spine Part Splitter(Avalonia, net10). Unity 게임에서 뽑은 Spine 텍스처 PNG를 `.atlas` 기준으로 파츠별 PNG로 분리, 폴더 일괄 처리, 아틀라스 재패킹. `AssetTool.Core`(로직), `AssetTool.Mcp`(MCP 서버) 분리.
- **종류:** 게임 모드가 아닌 앱/도구
- **최근 수정일(파일 기준):** 2026-11-05
- **git 원격:** `H:\local-repos\asset`
- **최근 커밋:**
  - `f50bbb2 Add GitHub Sponsors funding configuration`
  - `176e8dd Merge pull request #21 from magjangin/spine-anim-skins`
  - `b185cba spine_export_animations: 스킨이 있는 보스를 스킨별 폴더로 추출, 미리보기 시트 추가`
  - `b0da962 Merge pull request #20 from magjangin/spine-anim-export`
  - `6d5e4f9 spine_export_animations: 애니메이션별 투명 프레임과 스프라이트 시트 추출`
- **먼저 읽을 기존 문서:**
  - [README.md](README.md)

## 1. 게임 버전 및 고정 여부

- 게임 모드가 아니므로 해당 없음.

## 2. 런타임 백엔드 및 디컴파일 경로

- 게임 모드가 아님. 런타임/언어: .NET (`net10.0`)
- 모드/앱 프로젝트 `asset tool\asset tool.csproj` 타깃 프레임워크: `net10.0` (SDK 스타일)
- 모드/앱 프로젝트 `AssetTool.Core\AssetTool.Core.csproj` 타깃 프레임워크: `net10.0` (SDK 스타일)
- 모드/앱 프로젝트 `AssetTool.Mcp\AssetTool.Mcp.csproj` 타깃 프레임워크: `net10.0` (SDK 스타일)

## 3. 설계 의도와 우회 사유 (Rationale)

코드 주석과 기존 문서에서 작성자가 남긴 의도·주의 문장을 그대로 옮겼다(파일:줄).

- `README.md:107` — Spine 에디터에서 skel을 열고, 파츠 경로를 잡고, 애니메이션 탭에서 마커를 움직여 프레임을 찍던 작업을 CLI로 대신합니다. **라이선스가 있는 Spine 에디터(Pro, 3.8.x)** 가 필요하고, 기본 경로가 아니면 환경변수 `SPINE_CLI`나 `spineCliPath`로 `Spine.com`을 알려 주세요.
- `README.md:117` — 스킨**: `0914`처럼 몸통이 기본 스킨이 아니라 `origin`/`xiemei`(장면09), `Miku`/`RinLen`(장면10) 같은 스킨에만 있는 스켈레톤이 있습니다. 그대로 내보내면 그림자만 나옵니다. 다른 스킨이 프로젝트에 남아 있으면 스파인 PNG 내보내기가 기본 스킨의 첨부만 그리기 때문에, `skins: "all"`(기본값)은 스킨마다 "기본 스킨 + 그 스킨"만 가진 복
- `README.md:118` — 프레임 고르기(`pick: "largest"`)**: 기본은 `animation`의 `time` 한 프레임입니다. 노트 06~08은 `standby` 반복이 찌그러진 자세로 시작하고, 15번 노트는 `standby`가 없고, 일부 노트는 0초에 캐릭터가 아직 안 드러납니다. `largest`는 `candidateAnimations`(기본 `stand`/`standby*`)의 샘플 프레임 중 
- `README.md:121` — 파츠가 모자라면 `warnings`에 "not found"가 나옵니다(예: `0103_air`는 `0103/images_road/smoke`를 쓰는데 이 아틀라스에는 없음).
- `AssetTool.Core\AtlasExtractor.cs:77` — ownership got lost to a neighbour), keep the uncleaned crop instead.
- `AssetTool.Core\AtlasOverlapResolver.cs:31` — wiped out legitimate parts in the process (every note "shadow" - a dark
- `AssetTool.Core\AtlasOverlapResolver.cs:34` — then nothing is touched at all - which is right, because nothing can leak.
- `AssetTool.Core\AtlasParser.cs:52` — key/values some exporters add) are therefore ignored instead of being
- `AssetTool.Core\AtlasParser.cs:189` — it's ignored) instead of throwing and taking the file down, or
- `AssetTool.Core\BackgroundCutout.cs:9` — rectangles and can't damage a correct part; this is only a fallback for
- `AssetTool.Core\BackgroundCutout.cs:21` — erased every dark part touching the border (note shadows, dark outlines,
- `AssetTool.Core\BackgroundCutout.cs:120` — eye, an eye highlight, a skull mark on a note body (measured: a 6x6
- `AssetTool.Core\BatchProcessor.cs:16` — note textures) are copied as-is so the output is a complete set.
- `AssetTool.Core\BatchProcessor.cs:22` — instead of <output>\<atlas folder>\<atlas name>\<region>.png. Two atlases

## 4. 핵심 훅 시그니처와 주입 파이프라인

- **주요 소스 파일(큰 순):**
  - `AssetTool.Mcp\SpineTools.cs`
  - `AssetTool.Core\SpineRenderer.cs`
  - `asset tool\MainWindow.axaml.cs`
  - `AssetTool.Mcp\AssetTools.cs`
  - `AssetTool.Core\BatchProcessor.cs`
  - `AssetTool.Core\AtlasOverlapResolver.cs`
  - `AssetTool.Core\AtlasPacker.cs`
  - `AssetTool.Core\SkelSkinReader.cs`
  - `AssetTool.Core\BackgroundCutout.cs`
  - `AssetTool.Core\RgbaImage.cs`
  - `AssetTool.Core\AtlasParser.cs`
  - `AssetTool.Core\PagePngLocator.cs`

## 5. 시도했다가 실패한 것들 (Dead Ends)

코드 주석과 기존 문서에서 실패·크래시·금지 관련 문장을 그대로 옮겼다(파일:줄). 같은 방법을 다시 시도하기 전에 해당 위치를 읽을 것.

- `README.md:30` — 한 아틀라스에서 오류가 나도 기록만 하고 다음 아틀라스로 넘어갑니다.
- `README.md:52` — 무거운 작업은 백그라운드에서 돌아서 화면이 멈추지 않습니다. 오류는 프로그램을 끄지 않고 상태줄에 표시합니다.
- `asset tool\Program.cs:8` — Initialization code. Don't use any Avalonia, third-party APIs or any
- `asset tool\Program.cs:15` — Avalonia configuration, don't remove; also used by visual designer.
- `AssetTool.Core\AtlasExtractor.cs:16` — Pad stripped whitespace back using orig/offset, so the PNG has the
- `AssetTool.Core\AtlasOverlapResolver.cs:33` — normally packed atlas (most note/scene atlases) simply don't overlap, and
- `AssetTool.Core\AtlasPacker.cs:11` — Largest page edge. Parts that don't fit go to a further page.
- `AssetTool.Core\AtlasParser.cs:16` — Whitespace stripping: the original image size and where the packed
- `AssetTool.Core\AtlasParser.cs:48` — (some exporters indent attribute lines, some don't): a blank line ends a
- `AssetTool.Core\RgbaImage.cs:32` — (whose RGB is meaningless) don't bleed dark/white fringes into edges.

## 6. 빌드 명령 및 배포 경로

- 솔루션: `asset tool.slnx`
- `asset tool\asset tool.csproj`:
  ```powershell
  dotnet build "asset tool\asset tool.csproj" -c Release
  ```
- `AssetTool.Core\AssetTool.Core.csproj`:
  ```powershell
  dotnet build "AssetTool.Core\AssetTool.Core.csproj" -c Release
  ```
- `AssetTool.Mcp\AssetTool.Mcp.csproj`:
  ```powershell
  dotnet build "AssetTool.Mcp\AssetTool.Mcp.csproj" -c Release
  ```
- 빌드/배포 스크립트: `scripts\run-mcp.cmd`

## 7. 다음 작업자에게

- 작업 전 기존 문서(0절)부터 읽는다.
- 인게임 동작을 확인하면 3·5절의 **미확인** 항목을 실제 내용으로 바꾼다.
