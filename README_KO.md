# Elbow Fixer

[English](README.md) | 한국어 | [日本語](README_JA.md)

> VRChat 아바타의 팔꿈치와 팔뚝 비틀림을 비파괴 방식으로 보정하는 도구입니다.

## 요구 사항

- Unity 2022.3.22f1
- [Modular Avatar](https://modular-avatar.nadena.dev/)

## 설치

[VPM 저장소](https://koz39.github.io/vpm-listing/)에서 "Add to VCC"로 저장소를 추가한 뒤, VCC 또는 ALCOM의 프로젝트 관리에서 Elbow Fixer 패키지를 추가합니다.

## 사용법

1. 하이어라키에서 아바타를 우클릭하고 Elbow Fixer > Setup을 선택합니다.
2. 몸과 옷 소매가 대상 메시에 자동으로 추가됩니다. 아직 실험적인 기능이니 인스펙터에서 목록을 확인해야 합니다.
3. 아바타를 빌드할 때 자동으로 적용됩니다.

## 주의 사항

- Elbow Fixer는 Prefabulous의 PA Generate Twist Bones와 함께 사용할 수 없습니다. 아바타에 둘 다 추가되어 있으면 빌드가 오류로 중단됩니다.

## 참고 자료

- [Prefabulous Universal - Generate Twist Bones](https://docs.hai-vr.dev/docs/products/prefabulous/universal/generate-twist-bones)
- [Avatar Elbow Repair](https://koimoe.booth.pm/items/8844538)
