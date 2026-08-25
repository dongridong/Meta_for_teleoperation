# IOBT 스키마 추가분 — 그림 작성용 자료

`main` 브랜치 대비 **새로 추가된 데이터**만 정리한 문서입니다.
(전체 스키마는 `SCHEMA.md` / `SCHEMA_KOR.md` 참조)

---

## 1. 한 줄 요약

`main`의 프레임에는 `hmd`, `hands`, `controllers` 세 필드가 있었습니다.
여기에 **`body` 필드 하나가 통째로 추가**되었습니다 — Meta Inside-Out Body
Tracking(IOBT)이 헤드셋 카메라로 관측한 **상체 18관절 스켈레톤**입니다.

기존 필드 3개는 **한 글자도 바뀌지 않았습니다.** 순수 추가(superset)이므로
기존 수신기는 `body` 키를 무시하면 그대로 동작합니다.

```
main             :  { timestamp, frame, coordinate_system, hmd, hands, controllers }
feature/IOBT     :  { timestamp, frame, coordinate_system, hmd, hands, controllers, body }
                                                                                  ^^^^^^
                                                                                  ← 추가된 부분
```

---

## 2. 추가된 필드 구조

```json
"body": {
  "tracked": true,
  "fidelity": "high",
  "confidence": 0.97,
  "joints": [
    { "i": 0,  "valid": true, "position": [0.000, 0.000, 0.900], "rotation": [0, 0, 0, 1] },
    { "i": 1,  "valid": true, "position": [0.000, 0.000, 0.950], "rotation": [0, 0, 0, 1] },
    ...
    { "i": 17, "valid": true, "position": [0.310, -0.180, 1.170], "rotation": [0, 0, 0, 1] }
  ]
}
```

| 필드 | 타입 | 출력되는 값 |
| --- | --- | --- |
| `tracked` | bool | `true` / `false`. **false면 아래 3개 키가 전부 사라짐** |
| `fidelity` | string | `"high"` / `"low"` / `"unspecified"` 셋 중 하나 |
| `confidence` | float | `0.0` ~ `1.0` (실측 시 대개 `0.9` 이상) |
| `joints` | array | **정확히 18개**, `i` = 0..17 순서 고정 |

관절 1개의 형태:

| 필드 | 타입 | 값 |
| --- | --- | --- |
| `i` | int | `0` ~ `17` (관절 번호, 아래 표) |
| `valid` | bool | `false`면 `position`/`rotation`이 **생략됨** (0으로 채우지 않음) |
| `position` | `[x, y, z]` | 미터. 소수점 최대 6자리 |
| `rotation` | `[x, y, z, w]` | 쿼터니언. ⚠ `[w,x,y,z]` 아님 |

### `fidelity` 값의 의미

| 값 | 의미 |
| --- | --- |
| `"high"` | **진짜 IOBT.** 헤드셋 카메라가 실제 팔·몸통을 관측 중 |
| `"low"` | 런타임이 고정밀 모드를 거절. HMD + 컨트롤러로부터 **추론**된 자세 — 특히 팔꿈치가 부정확 |
| `"unspecified"` | 확장을 못 써서 런타임 기본값 적용 (Quest 3/3S 최신 OS에서는 사실상 IOBT) |

---

## 3. 좌표계 (그림 그릴 때 필수)

기존 필드와 **동일한 하나의 월드 좌표계**를 씁니다. `body`만의 별도 기준이 아닙니다.

| 항목 | 값 |
| --- | --- |
| 이름 | `robot_world` |
| 손 방향 | **오른손 좌표계** |
| 축 | **+X = 정면 / +Y = 왼쪽 / +Z = 위(하늘)** (FLU, ROS REP-103) |
| 단위 | 미터 |
| 원점 | 앱 시작(또는 recenter) 시점 사용자 **발밑 바닥**, 그때 바라보던 방향이 +X |

⚠️ **HMD 상대 좌표가 아닙니다.** HMD·손·컨트롤러·상체가 전부 같은 월드 좌표계에
들어 있습니다. 그래서 관절의 **Z값 = 바닥으로부터의 높이**입니다.

**투영 가이드**

- **정면도** (사람이 정면을 보고 서 있는 그림): 가로축 = `+Y`, 세로축 = `+Z`
  → 사람의 왼팔(`LEFT_*`)이 그림에서는 **오른쪽**에 옵니다 (거울상, 정상)
- **측면도** (사람 왼쪽에서 본 그림): 가로축 = `+X`(사람이 오른쪽을 봄), 세로축 = `+Z`

---

## 4. 관절 18개 — 번호와 이름

OpenXR `XrBodyJointFB`의 0~17번을 그대로 씁니다. **이 순서는 절대 바뀌지 않습니다.**

| i | 이름 | 부위 |
|---|---|---|
| 0 | `ROOT` | 골반 아래 기준점 |
| 1 | `HIPS` | 골반 |
| 2 | `SPINE_LOWER` | 척추 하부 |
| 3 | `SPINE_MIDDLE` | 척추 중부 |
| 4 | `SPINE_UPPER` | 척추 상부 |
| 5 | `CHEST` | 흉부 (양팔·목이 갈라지는 분기점) |
| 6 | `NECK` | 목 |
| 7 | `HEAD` | 머리 |
| 8 | `LEFT_SHOULDER` | 왼쪽 어깨 |
| 9 | `LEFT_SCAPULA` | 왼쪽 견갑골 |
| 10 | `LEFT_ARM_UPPER` | 왼쪽 위팔 시작 |
| 11 | `LEFT_ARM_LOWER` | **왼쪽 팔꿈치** ★ |
| 12 | `LEFT_WRIST_TWIST` | 왼쪽 손목 (손 스켈레톤과 만나는 지점) |
| 13 | `RIGHT_SHOULDER` | 오른쪽 어깨 |
| 14 | `RIGHT_SCAPULA` | 오른쪽 견갑골 |
| 15 | `RIGHT_ARM_UPPER` | 오른쪽 위팔 시작 |
| 16 | `RIGHT_ARM_LOWER` | **오른쪽 팔꿈치** ★ |
| 17 | `RIGHT_WRIST_TWIST` | 오른쪽 손목 (손 스켈레톤과 만나는 지점) |

★ **팔꿈치(11 / 16)가 IOBT의 핵심 기여분**입니다 — 기존 HMD·손 데이터만으로는
알 수 없었던 정보입니다.

> **왜 18개인가?** `XR_FB_body_tracking`의 기본 관절 세트는 70개인데, 0~17번이
> 몸통+팔이고 18~69번이 양손입니다. 손은 이미 `hands` 필드에 MediaPipe 21점으로
> **더 높은 정밀도로** 나가므로 18~69번은 버립니다. → `body`와 `hands` 사이에
> **중복이 0**입니다.
>
> ⚠️ **하체는 없습니다.** 다리·발 관절은 애초에 이 세트에 포함되지 않습니다.

---

## 5. 뼈대 연결 (선을 그을 쌍)

부모 → 자식. 그림에서 **선분 17개**를 그으면 됩니다.

```
척추:    0 ROOT → 1 HIPS → 2 SPINE_LOWER → 3 SPINE_MIDDLE → 4 SPINE_UPPER → 5 CHEST
머리:    5 CHEST → 6 NECK → 7 HEAD
왼팔:    5 CHEST → 9 LEFT_SCAPULA  → 8 LEFT_SHOULDER  → 10 LEFT_ARM_UPPER  → 11 LEFT_ARM_LOWER  → 12 LEFT_WRIST_TWIST
오른팔:  5 CHEST → 14 RIGHT_SCAPULA → 13 RIGHT_SHOULDER → 15 RIGHT_ARM_UPPER → 16 RIGHT_ARM_LOWER → 17 RIGHT_WRIST_TWIST
```

인덱스 쌍 형태:

```
(0,1) (1,2) (2,3) (3,4) (4,5)
(5,6) (6,7)
(5,9) (9,8) (8,10) (10,11) (11,12)
(5,14) (14,13) (13,15) (15,16) (16,17)
```

---

## 6. 참조 자세 (그림용 실제 좌표값)

팔꿈치를 약 90°로 굽힌 자연스러운 텔레오퍼레이션 자세입니다.
단위 미터, `robot_world` 기준. **이 값으로 바로 그리면 됩니다.**

| i | 이름 | x (앞) | y (왼쪽) | z (높이) |
|---|---|---|---|---|
| 0 | `ROOT` | 0.00 | 0.00 | 0.90 |
| 1 | `HIPS` | 0.00 | 0.00 | 0.95 |
| 2 | `SPINE_LOWER` | 0.00 | 0.00 | 1.05 |
| 3 | `SPINE_MIDDLE` | 0.00 | 0.00 | 1.15 |
| 4 | `SPINE_UPPER` | 0.00 | 0.00 | 1.25 |
| 5 | `CHEST` | 0.00 | 0.00 | 1.35 |
| 6 | `NECK` | 0.00 | 0.00 | 1.47 |
| 7 | `HEAD` | 0.00 | 0.00 | 1.57 |
| 8 | `LEFT_SHOULDER` | 0.00 | **+0.18** | 1.40 |
| 9 | `LEFT_SCAPULA` | 0.00 | **+0.07** | 1.38 |
| 10 | `LEFT_ARM_UPPER` | 0.00 | **+0.18** | 1.38 |
| 11 | `LEFT_ARM_LOWER` ★ | 0.06 | **+0.18** | 1.11 |
| 12 | `LEFT_WRIST_TWIST` | 0.31 | **+0.18** | 1.17 |
| 13 | `RIGHT_SHOULDER` | 0.00 | **−0.18** | 1.40 |
| 14 | `RIGHT_SCAPULA` | 0.00 | **−0.07** | 1.38 |
| 15 | `RIGHT_ARM_UPPER` | 0.00 | **−0.18** | 1.38 |
| 16 | `RIGHT_ARM_LOWER` ★ | 0.06 | **−0.18** | 1.11 |
| 17 | `RIGHT_WRIST_TWIST` | 0.31 | **−0.18** | 1.17 |

이 자세의 인체 치수 (실제 프로젝트의 mock 모델과 동일):

| 항목 | 값 |
| --- | --- |
| 골반 높이 | 0.95 m |
| 어깨 반폭 | 0.18 m (좌우 총 0.36 m) |
| 위팔 길이 | 0.28 m |
| 아래팔 길이 | 0.26 m |
| 팔꿈치 굽힘각 | 약 89° |

---

## 7. `hands`와 만나는 지점 (그림에서 중요)

상체 스켈레톤은 **손목에서 끝나고**, 거기서 손 스켈레톤이 이어받습니다.

```
body[12] LEFT_WRIST_TWIST   ≡  hands[left].landmarks[0]  (WRIST)
body[17] RIGHT_WRIST_TWIST  ≡  hands[right].landmarks[0] (WRIST)
```

두 관절은 **공간상 같은 위치**입니다. 그림에서 상체(18관절)와 손(21×2관절)을
이 점에서 이어 붙이면 전체 스켈레톤이 완성됩니다.

```
   HEAD(7)
     │
   NECK(6)
     │
  CHEST(5) ────┬──── SCAPULA ── SHOULDER ── ARM_UPPER ── [ARM_LOWER 팔꿈치] ── WRIST_TWIST ─── ✋ hands(21점)
     │         └──── (좌우 대칭)
  SPINE(4,3,2)
     │
   HIPS(1)
     │
   ROOT(0)
```

---

## 8. 데이터 소스 관계 (그림으로 표현하면 좋은 부분)

`hands` / `controllers` / `body`는 **모드 전환이 아니라 서로 독립된 3개 소스**입니다.
한 프레임 안에 임의의 조합이 들어올 수 있습니다.

| 소스 | 추적 방식 | 한 프레임 내 관계 |
| --- | --- | --- |
| `hands` | OpenXR 핸드트래킹 (카메라) | 독립 |
| `controllers` | 컨트롤러 6DoF + 버튼/트리거 | 독립 |
| `body` ← **추가분** | IOBT (헤드셋 카메라가 몸통·팔 관측) | 독립 |

즉 **컨트롤러를 쥔 채로도 상체 정보가 동시에 나옵니다.** 컨트롤러가 잡히면서
팔꿈치도 같이 잡히는 상태가 정상입니다.

---

## 9. 값이 안 나오는 경우 (생략 규칙)

| 상황 | 출력 |
| --- | --- |
| 몸 추적 안 됨 | `"body": {"tracked": false}` — `fidelity`/`confidence`/`joints` **키 자체가 없음** |
| 특정 관절만 소실 | `{"i": 11, "valid": false}` — `position`/`rotation` **키 자체가 없음** |
| 권한 미승인 | 승인 전까지 `tracked: false`. 승인 즉시 자동으로 나오기 시작 (재시작 불필요) |
| 바디트래킹 미지원 기기 | 항상 `tracked: false` (다른 필드는 정상 동작) |
| 앱 설정에서 끔 (`publishBody=false`) | `tracked: false`. **`body` 키는 사라지지 않음** — 스키마 모양 항상 동일 |

---

## 10. 부가 추가분 (스키마 외)

데이터 스키마는 아니지만 `main` 대비 같이 추가된 것들입니다.

| 항목 | 내용 |
| --- | --- |
| Android 권한 | `com.oculus.permission.BODY_TRACKING` (런타임 승인 필요) |
| Android feature | `com.oculus.software.body_tracking` (`required=false`) |
| OpenXR 확장 | `XR_FB_body_tracking`, `XR_META_body_tracking_fidelity` |
| 외부 의존성 | **없음.** Meta XR SDK 없이 자체 OpenXR feature로 확장을 직접 호출 |

---

## 부록: 팔꿈치 각도 계산법

각도(스칼라)는 **출력되지 않습니다.** 필요하면 세 관절 위치로 직접 구합니다
(오른팔 기준, 좌표계와 무관하게 동일한 값이 나옴):

```python
import numpy as np
u = np.array(p[15]) - np.array(p[16])   # RIGHT_ARM_UPPER   - RIGHT_ARM_LOWER
f = np.array(p[17]) - np.array(p[16])   # RIGHT_WRIST_TWIST - RIGHT_ARM_LOWER
angle = np.degrees(np.arccos(np.dot(u, f) / (np.linalg.norm(u) * np.linalg.norm(f))))
# 180° = 팔을 완전히 편 상태, 값이 작을수록 많이 굽힌 상태
```
