# Elbow Fixer

[English](README.md) | [한국어](README_KO.md) | 日本語

> VRChatアバターの肘と前腕のねじれを非破壊で補正するツールです。

## 動作環境

- Unity 2022.3.22f1
- [Modular Avatar](https://modular-avatar.nadena.dev/)

## インストール

[VPMリポジトリ](https://koz39.github.io/vpm-listing/)で「Add to VCC」を押してリポジトリを追加した後、VCCまたはALCOMのプロジェクトの管理画面でElbow Fixerパッケージを追加します。

## 使い方

1. ヒエラルキーでアバターを右クリックし、Elbow Fixer > Setup を選択します。
2. ボディと服の袖が対象メッシュに自動で追加されます。まだ実験的な機能のため、インスペクターでリストを確認する必要があります。
3. アバターのビルド時に自動で適用されます。

## 注意事項

- Elbow FixerはPrefabulousのPA Generate Twist Bonesと併用できません。アバターに両方が含まれている場合、ビルドはエラーで中断されます。

## 参考資料

- [Prefabulous Universal - Generate Twist Bones](https://docs.hai-vr.dev/docs/products/prefabulous/universal/generate-twist-bones)
- [Avatar Elbow Repair](https://koimoe.booth.pm/items/8844538)
