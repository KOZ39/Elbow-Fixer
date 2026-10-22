# 변경 내역

## Unreleased

### Changed

- NDMF 1.8.0 이상 필요
- 다른 아바타 아래의 Elbow Fixer와 메시는 바깥 아바타에서 제외

### Fixed

- 보정된 메시에 대한 다른 도구의 NDMF 콘솔 오류가 원본 메시를 가리키지 않는 문제

## v0.2.0 (2026-10-19)

### Changed

- 팔뚝 가중치를 인접한 비틀림 본 세 개까지 분배

## v0.1.2 (2026-10-13)

### Fixed

- MA Shape Changer로 팔뚝 메시를 숨기는 토글을 기본값으로 설정 시 빌드 오류
- MA Shape Changer로 팔뚝 메시를 숨기는 토글 사용 시 해당 구간의 비틀림 보정 누락
- 팔뚝·손의 Position·Scale Constraint(VRC, Unity)를 비틀림 Constraint로 오인하는 문제
- 가중치가 없는 정점이 있는 대상 메시 사용 시 빌드 오류
- Prefabulous PA Generate Twist Bones와 함께 사용 시 팔뚝 비틀림 본 중복 감지 누락

## v0.1.1 (2026-10-10)

### Fixed

- 빌드 시 발견된 설정 오류가 NDMF 콘솔에 내부 오류로 표시되는 문제
- 대상 메시 설정 오류 시 Avatar Optimizer의 알 수 없는 컴포넌트 경고가 함께 표시되는 문제

## v0.1.0 (2026-10-10)

### Added

- 최초 출시
