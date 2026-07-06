# setup_junction.ps1
# C# 저장소의 docs 폴더를 Obsidian.Agent 지식 베이스로 디렉터리 정션(Junction) 연결해 주는 셋업 스크립트

$TargetJunction = "../Obsidian.Agent/MundusVivens/docs"
$SourceDocs = "./docs"

# 상대 경로를 절대 경로로 명확하게 변환하여 mklink 전달
$AbsoluteTarget = Resolve-Path -Path (Join-Path $PSScriptRoot $TargetJunction) -ErrorAction SilentlyContinue
if (-not $AbsoluteTarget) {
    # Resolve-Path가 폴더가 없어서 실패할 경우를 위한 수동 절대 경로 빌드
    $AbsoluteTarget = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $TargetJunction))
}

$AbsoluteSource = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $SourceDocs))

if (Test-Path $AbsoluteTarget) {
    Write-Host "⚠️ 이미 정션이나 폴더가 존재합니다: $AbsoluteTarget" -ForegroundColor Yellow
} else {
    Write-Host "🔄 정션 생성 중..." -ForegroundColor Cyan
    Write-Host "  - Target: $AbsoluteTarget"
    Write-Host "  - Source: $AbsoluteSource"
    
    cmd /c mklink /j `"$AbsoluteTarget`" `"$AbsoluteSource`"
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✅ Obsidian.Agent 지식 베이스로의 문서 정션 생성에 성공했습니다!" -ForegroundColor Green
    } else {
        Write-Host "❌ 정션 생성에 실패했습니다. 경로를 확인해 주세요." -ForegroundColor Red
    }
}
