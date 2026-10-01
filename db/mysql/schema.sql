-- 병원 무인수납 키오스크 시뮬레이터 — MySQL 8 스키마
-- 금액은 원 단위 BIGINT. 시간은 서버 로컬(Asia/Seoul) 기준 DATETIME.

CREATE TABLE IF NOT EXISTS patient (
    patient_no   VARCHAR(20)  NOT NULL PRIMARY KEY,
    name         VARCHAR(50)  NOT NULL,
    birth_date   DATE         NOT NULL,
    phone_last4  CHAR(4)      NOT NULL
);

CREATE TABLE IF NOT EXISTS visit (
    patient_no   VARCHAR(20)  NOT NULL,
    visit_date   DATE         NOT NULL,
    department   VARCHAR(30)  NOT NULL,
    PRIMARY KEY (patient_no, visit_date, department),
    FOREIGN KEY (patient_no) REFERENCES patient(patient_no)
);

CREATE TABLE IF NOT EXISTS payment (
    id               BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
    idempotency_key  VARCHAR(64)  NOT NULL,
    patient_no       VARCHAR(20)  NOT NULL,
    method           VARCHAR(10)  NOT NULL,          -- Card / Cash
    amount           BIGINT       NOT NULL,
    status           VARCHAR(12)  NOT NULL,          -- Pending / Approved / Failed / Cancelled
    approval_no      VARCHAR(20)  NULL,
    fail_reason      VARCHAR(200) NULL,
    cash_received    BIGINT       NOT NULL DEFAULT 0,
    change_amount    BIGINT       NOT NULL DEFAULT 0,
    created_at       DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY uq_payment_key (idempotency_key)      -- 같은 요청 두 번 → 두 번째 INSERT 실패 = 이중 결제 방지
);

CREATE TABLE IF NOT EXISTS receivable (
    id           BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
    patient_no   VARCHAR(20)  NOT NULL,
    kind         VARCHAR(12)  NOT NULL,              -- Treatment / Certificate
    department   VARCHAR(30)  NOT NULL,
    visit_date   DATE         NOT NULL,
    status       VARCHAR(10)  NOT NULL DEFAULT 'Unpaid',
    payment_id   BIGINT       NULL,
    KEY ix_receivable_patient (patient_no, status),
    FOREIGN KEY (patient_no) REFERENCES patient(patient_no),
    FOREIGN KEY (payment_id) REFERENCES payment(id)
);

CREATE TABLE IF NOT EXISTS receivable_item (
    receivable_id BIGINT      NOT NULL,
    seq           INT         NOT NULL,
    code          VARCHAR(20) NOT NULL,
    name          VARCHAR(100) NOT NULL,
    unit_price    BIGINT      NOT NULL,
    quantity      INT         NOT NULL,
    PRIMARY KEY (receivable_id, seq),
    FOREIGN KEY (receivable_id) REFERENCES receivable(id)
);

-- 결제 1건이 어떤 수납 건들을 묶어 냈는지
CREATE TABLE IF NOT EXISTS payment_target (
    payment_id    BIGINT NOT NULL,
    receivable_id BIGINT NOT NULL,
    PRIMARY KEY (payment_id, receivable_id),
    FOREIGN KEY (payment_id) REFERENCES payment(id),
    FOREIGN KEY (receivable_id) REFERENCES receivable(id)
);

CREATE TABLE IF NOT EXISTS certificate (
    document_no  VARCHAR(30)  NOT NULL PRIMARY KEY,
    patient_no   VARCHAR(20)  NOT NULL,
    type_code    VARCHAR(20)  NOT NULL,
    visit_date   DATE         NOT NULL,
    issued_at    DATETIME     NOT NULL,
    FOREIGN KEY (patient_no) REFERENCES patient(patient_no)
);

CREATE TABLE IF NOT EXISTS queue_counter (
    queue_date   DATE        NOT NULL,
    category     VARCHAR(12) NOT NULL,               -- Payment / Admission / Certificate
    last_issued  INT         NOT NULL,
    last_called  INT         NOT NULL,
    PRIMARY KEY (queue_date, category)
);

CREATE TABLE IF NOT EXISTS waiting (
    patient_no    VARCHAR(20) NOT NULL,
    department    VARCHAR(30) NOT NULL,
    doctor        VARCHAR(30) NOT NULL,
    reception_at  DATETIME    NOT NULL,
    status        VARCHAR(12) NOT NULL,              -- Waiting / InTreatment / Done
    PRIMARY KEY (patient_no, department, reception_at),
    FOREIGN KEY (patient_no) REFERENCES patient(patient_no)
);
