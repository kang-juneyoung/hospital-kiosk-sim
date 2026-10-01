-- 시연용 가상 데이터 (SampleData.cs와 같은 내용). 실제 환자 정보가 아니다.
-- 진료일은 실행한 날 기준(CURDATE)으로 넣는다.

INSERT INTO patient (patient_no, name, birth_date, phone_last4) VALUES
 ('10000001', '홍길동', '1980-05-12', '1234'),
 ('10000002', '김영희', '1992-11-03', '5678'),
 ('10000003', '이수',   '1975-01-30', '9012');

INSERT INTO visit (patient_no, visit_date, department) VALUES
 ('10000001', CURDATE(), '내과'),
 ('10000001', CURDATE() - INTERVAL 1 DAY, '영상의학과'),
 ('10000002', CURDATE(), '정형외과');

INSERT INTO receivable (id, patient_no, kind, department, visit_date) VALUES
 (101, '10000001', 'Treatment', '내과',       CURDATE()),
 (102, '10000001', 'Treatment', '영상의학과', CURDATE() - INTERVAL 1 DAY),
 (103, '10000002', 'Treatment', '정형외과',   CURDATE());

INSERT INTO receivable_item (receivable_id, seq, code, name, unit_price, quantity) VALUES
 (101, 1, 'AA157', '재진 진찰료',  12800, 1),
 (101, 2, 'B1010', '일반혈액검사',  4300, 1),
 (101, 3, 'C5211', '주사료',        2150, 2),
 (102, 1, 'G2101', '흉부 X-ray',    9600, 1),
 (103, 1, 'AA154', '초진 진찰료',  18700, 1),
 (103, 2, 'MM101', '물리치료',      3200, 3);

INSERT INTO waiting (patient_no, department, doctor, reception_at, status) VALUES
 ('10000002', '정형외과', '박의사', TIMESTAMP(CURDATE(), '09:05:00'), 'InTreatment'),
 ('10000003', '정형외과', '박의사', TIMESTAMP(CURDATE(), '09:12:00'), 'Waiting'),
 ('10000001', '정형외과', '박의사', TIMESTAMP(CURDATE(), '09:20:00'), 'Waiting');
