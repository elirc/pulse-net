-- Frozen pre-migration schema. No application data or credentials.
CREATE TABLE "Annotations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Annotations" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Date" TEXT NOT NULL,
    "Content" TEXT NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);
CREATE TABLE "CohortPersons" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CohortPersons" PRIMARY KEY,
    "CohortId" TEXT NOT NULL,
    "PersonId" TEXT NOT NULL,
    "AddedAt" INTEGER NOT NULL
);
CREATE TABLE "Cohorts" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Cohorts" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Type" TEXT NOT NULL,
    "RulesJson" TEXT NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);
CREATE TABLE "DashboardTiles" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_DashboardTiles" PRIMARY KEY,
    "DashboardId" TEXT NOT NULL,
    "InsightId" TEXT NOT NULL,
    "LayoutJson" TEXT NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);
CREATE TABLE "Dashboards" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Dashboards" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);
CREATE TABLE "DeadLetterEvents" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_DeadLetterEvents" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "Error" TEXT NOT NULL,
    "Attempts" INTEGER NOT NULL,
    "FailedAt" INTEGER NOT NULL
);
CREATE TABLE "EventDefinitions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_EventDefinitions" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "FirstSeenAt" INTEGER NOT NULL,
    "LastSeenAt" INTEGER NOT NULL
);
CREATE TABLE "Events" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Events" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "DistinctId" TEXT NOT NULL,
    "PersonId" TEXT NULL,
    "Timestamp" INTEGER NOT NULL,
    "PropertiesJson" TEXT NOT NULL
);
CREATE TABLE "ExportJobs" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ExportJobs" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Type" TEXT NOT NULL,
    "Format" TEXT NOT NULL,
    "ParamsJson" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "ResultContent" TEXT NULL,
    "ContentType" TEXT NULL,
    "RowCount" INTEGER NOT NULL,
    "Error" TEXT NULL,
    "CreatedAt" INTEGER NOT NULL,
    "CompletedAt" INTEGER NULL
);
CREATE TABLE "FeatureFlags" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_FeatureFlags" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Key" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Type" TEXT NOT NULL,
    "Active" INTEGER NOT NULL,
    "RolloutPercentage" REAL NOT NULL,
    "FiltersJson" TEXT NOT NULL,
    "VariantsJson" TEXT NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);
CREATE TABLE "Insights" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Insights" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Type" TEXT NOT NULL,
    "ConfigJson" TEXT NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);
CREATE TABLE "PersonDistinctIds" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_PersonDistinctIds" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "DistinctId" TEXT NOT NULL,
    "PersonId" TEXT NOT NULL
);
CREATE TABLE "PersonalApiKeys" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_PersonalApiKeys" PRIMARY KEY,
    "UserId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "KeyHash" TEXT NOT NULL,
    "KeySuffix" TEXT NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);
CREATE TABLE "Persons" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Persons" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "PropertiesJson" TEXT NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);
CREATE TABLE "ProjectMemberships" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ProjectMemberships" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);
CREATE TABLE "Projects" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Projects" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "ApiKey" TEXT NOT NULL,
    "ReadKey" TEXT NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);
CREATE TABLE "PropertyDefinitions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_PropertyDefinitions" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "PropertyType" TEXT NOT NULL,
    "FirstSeenAt" INTEGER NOT NULL,
    "LastSeenAt" INTEGER NOT NULL
);
CREATE TABLE "QueuedEvents" (
    "Seq" INTEGER NOT NULL CONSTRAINT "PK_QueuedEvents" PRIMARY KEY AUTOINCREMENT,
    "ProjectId" TEXT NOT NULL,
    "PayloadJson" TEXT NOT NULL,
    "Attempts" INTEGER NOT NULL,
    "EnqueuedAt" INTEGER NOT NULL
);
CREATE TABLE "Users" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY,
    "Email" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "PasswordHash" TEXT NOT NULL,
    "CreatedAt" INTEGER NOT NULL
);
CREATE INDEX "IX_Annotations_ProjectId_Date" ON "Annotations" ("ProjectId", "Date");
CREATE UNIQUE INDEX "IX_CohortPersons_CohortId_PersonId" ON "CohortPersons" ("CohortId", "PersonId");
CREATE INDEX "IX_Cohorts_ProjectId" ON "Cohorts" ("ProjectId");
CREATE INDEX "IX_DashboardTiles_DashboardId" ON "DashboardTiles" ("DashboardId");
CREATE INDEX "IX_Dashboards_ProjectId" ON "Dashboards" ("ProjectId");
CREATE INDEX "IX_DeadLetterEvents_ProjectId" ON "DeadLetterEvents" ("ProjectId");
CREATE UNIQUE INDEX "IX_EventDefinitions_ProjectId_Name" ON "EventDefinitions" ("ProjectId", "Name");
CREATE INDEX "IX_Events_ProjectId_Name_Timestamp" ON "Events" ("ProjectId", "Name", "Timestamp");
CREATE INDEX "IX_Events_ProjectId_PersonId_Timestamp" ON "Events" ("ProjectId", "PersonId", "Timestamp");
CREATE INDEX "IX_ExportJobs_ProjectId_CreatedAt" ON "ExportJobs" ("ProjectId", "CreatedAt");
CREATE UNIQUE INDEX "IX_FeatureFlags_ProjectId_Key" ON "FeatureFlags" ("ProjectId", "Key");
CREATE INDEX "IX_Insights_ProjectId" ON "Insights" ("ProjectId");
CREATE INDEX "IX_PersonDistinctIds_PersonId" ON "PersonDistinctIds" ("PersonId");
CREATE UNIQUE INDEX "IX_PersonDistinctIds_ProjectId_DistinctId" ON "PersonDistinctIds" ("ProjectId", "DistinctId");
CREATE UNIQUE INDEX "IX_PersonalApiKeys_KeyHash" ON "PersonalApiKeys" ("KeyHash");
CREATE INDEX "IX_PersonalApiKeys_UserId" ON "PersonalApiKeys" ("UserId");
CREATE INDEX "IX_Persons_ProjectId" ON "Persons" ("ProjectId");
CREATE UNIQUE INDEX "IX_ProjectMemberships_ProjectId_UserId" ON "ProjectMemberships" ("ProjectId", "UserId");
CREATE INDEX "IX_ProjectMemberships_UserId" ON "ProjectMemberships" ("UserId");
CREATE UNIQUE INDEX "IX_Projects_ApiKey" ON "Projects" ("ApiKey");
CREATE UNIQUE INDEX "IX_Projects_ReadKey" ON "Projects" ("ReadKey");
CREATE UNIQUE INDEX "IX_PropertyDefinitions_ProjectId_Name" ON "PropertyDefinitions" ("ProjectId", "Name");
CREATE INDEX "IX_QueuedEvents_ProjectId" ON "QueuedEvents" ("ProjectId");
CREATE UNIQUE INDEX "IX_Users_Email" ON "Users" ("Email");
