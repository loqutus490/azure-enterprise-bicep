param environment string
param amount int

@description('Email addresses to receive budget alert notifications.')
param contactEmails array = []
@description('Monthly budget start date (YYYY-MM-DD).')
param startDate string = utcNow('yyyy-MM-01')

var budgetNotifications = length(contactEmails) > 0
  ? {
      actualGreaterThan80Percent: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        contactEmails: contactEmails
        thresholdType: 'Actual'
      }
    }
  : {}

resource budget 'Microsoft.Consumption/budgets@2021-10-01' = {
  name: 'budget-${environment}'
  properties: {
    category: 'Cost'
    amount: amount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: startDate
    }
    notifications: budgetNotifications
  }
}
