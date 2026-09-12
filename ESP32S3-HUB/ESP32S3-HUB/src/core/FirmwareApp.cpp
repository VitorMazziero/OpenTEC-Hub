// Deliberately one translation unit during the v8 -> v9 migration.
//
// The .inc boundaries isolate responsibilities without changing initialization order
// or the legacy global-state ABI. New components are ordinary .h/.cpp modules. A later
// release can convert each legacy fragment independently after hardware equivalence.
#include "AppContext.h"
#include "../storage/Settings.h"
#include "../protocol/Mailboxes.h"
#include "../network/NodeDiagTask.h"
#include "../network/HttpServer.h"
#include "Runtime.h"
#include "../protocol/Commands.h"
#include "../sensor/Telemetry.h"
#include "../devices/AgitatorFoam.h"
#include "../sensor/SensorUart.h"
