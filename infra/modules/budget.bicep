param environment string
param amount int

resource budget 'Microsoft.Consumption/budgets@2021-10-01' = {
  name: 'budget-${environment}'
  properties: {
    category: 'Cost'
    amount: amount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: '2025-01-01'
    }
    notifications: {
      actualGreaterThan80Percent: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        contactEmails: []
        thresholdType: 'Actual'
      }
    }
  }
}
