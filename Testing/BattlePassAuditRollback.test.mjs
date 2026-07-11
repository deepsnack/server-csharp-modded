import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const read = path => fs.readFileSync(new URL('../' + path, import.meta.url), 'utf8');
const models = read('Libraries/SPTarkov.Server.Core/BattlePass/Administration/BattlePassChangeModels.cs');
const review = read('Libraries/SPTarkov.Server.Core/BattlePass/Administration/BattlePassReviewService.cs');
const controller = read('Libraries/SPTarkov.Server.Core/BattlePass/Administration/BattlePassReviewController.cs');
const auditController = read('Libraries/SPTarkov.Server.Core/BattlePass/Administration/BattlePassAuditController.cs');
const notifier = read('Libraries/SPTarkov.Server.Core/BattlePass/Administration/BattlePassReviewResultNotifier.cs');
const handlers = [
    'ShopChangeHandler.cs', 'TaskChangeHandler.cs', 'TrackChangeHandler.cs', 'LotteryChangeHandler.cs',
    'TraderChangeHandler.cs', 'RecipeChangeHandler.cs', 'ItemsChangeHandler.cs', 'FleaChangeHandler.cs',
].map(name => read('Libraries/SPTarkov.Server.Core/BattlePass/Administration/' + name));
const auditHtml = read('Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/admin/audit.html');
const auditJs = read('Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/admin/audit.js');

test('collaborator grant no longer owns notification email fields', () => {
    const grant = models.slice(models.indexOf('public class BpCollaboratorGrant'), models.indexOf('public class BpAuditLogEntry'));
    assert.doesNotMatch(grant, /EmailVerifiedUtc|public string\? Email/);
});

test('review notifications resolve collaborators and batch approval is aggregated', () => {
    assert.match(notifier, /ResolveByProfileId\(profileId\)/);
    assert.match(notifier, /NotifySubmitted/);
    assert.match(notifier, /NotifyApproved/);
    assert.match(notifier, /NotifyApprovedBatch/);
    assert.match(notifier, /NotifyRejected/);
    assert.match(review, /NotifySubmitted\(change\)/);
    assert.match(controller, /sendNotification:\s*false/);
    assert.match(controller, /NotifyApprovedBatch\(approvedChanges\)/);
});

test('audit records submit approve reject and protects rollback with revision checks', () => {
    assert.match(review, /SaveAudit\("submit"/);
    assert.match(review, /"approve"/);
    assert.match(review, /"reject"/);
    assert.match(review, /ResultRevision/);
    assert.match(review, /RollbackAudit/);
    assert.match(review, /RolledBack/);
    for (const handler of handlers) assert.match(handler, /RestoreSnapshot\(/);
});

test('audit API is admin-only and exposes configurable retention plus one-click rollback UI', () => {
    assert.match(auditController, /principal\?\.IsAdmin == true/);
    assert.match(auditController, /SaveAuditLogRetentionDays/);
    assert.match(auditController, /PurgeExpiredAudit/);
    assert.match(auditController, /\{auditId\}\/rollback/);
    assert.match(auditHtml, /日志保留/);
    assert.match(auditJs, /一键回溯/);
    assert.match(auditJs, /isCollaborator\(\)/);
});
